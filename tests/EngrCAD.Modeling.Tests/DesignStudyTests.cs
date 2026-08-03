using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The design-study loop, verified against closed forms rather than against "it improved".
///
/// <para>The headline fixture is a cantilever, because its minimum-mass depth for a stated
/// tip-deflection limit is analytic: with delta = P·L³/(3EI) and I = b·d³/12,
/// delta = 4PL³/(E·b·d³), so the lightest beam that meets the limit is
/// d* = cbrt(4PL³/(E·b·delta)). Mass increases with d and deflection falls with it, so the
/// optimum sits exactly ON the constraint — which is what makes the binding-constraint
/// report checkable too.</para>
/// </summary>
public sealed class DesignStudyTests
{
    // The beam: 200 long, 20 wide, depth driven; 500 N at the tip; 1 mm allowed.
    private const double Length = 200;
    private const double Width = 20;
    private const double TipLoad = 500;
    private const double DeflectionLimit = 1.0;

    private static double Modulus => Materials.Steel.YoungsModulus;

    /// <summary>The analytic answer: the shallowest beam whose tip deflection is exactly
    /// the limit.</summary>
    private static double AnalyticDepth(double limit) =>
        Math.Cbrt(4 * TipLoad * Length * Length * Length / (Modulus * Width * limit));

    /// <summary>Mass in grams of a solid rectangular steel beam of this depth.</summary>
    private static double AnalyticMassGrams(double depth) =>
        ModelUnits.MassToGrams(Length * Width * depth * Materials.Steel.Density);

    /// <summary>The tip deflection, read off the REGENERATED solid: the depth comes from the
    /// part's own bounds, so the constraint measures the model rather than restating the
    /// parameter that was written into it.</summary>
    private static double TipDeflection(Part part)
    {
        double depth = part.Bounds().Size.Z;
        double second = Width * depth * depth * depth / 12;
        return TipLoad * Length * Length * Length / (3 * Modulus * second);
    }

    private static double MassGrams(Part part) => part.MassGrams()!.Value;

    private static (Part Part, ExtrudeSketchFeature Extrude) Cantilever(double startDepth)
    {
        var extrude = new ExtrudeSketchFeature(Sketch.Rectangle(Length, Width)) { Height = startDepth };
        var history = new FeatureHistory();
        history.Add(extrude);
        return (history.ToPart("beam").Of(Materials.Steel), extrude);
    }

    // ---- the analytic optimum ---------------------------------------------------

    [Fact]
    public void MinimumMassDepth_LandsOnTheAnalyticOptimum_AndNamesTheDeflectionLimit()
    {
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(
            part, [depth], MassGrams,
            [StudyConstraint.AtMost("tip deflection", TipDeflection, DeflectionLimit)]);

        Assert.True(result.Succeeded);
        Assert.True(result.Feasible);
        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);

        // The tolerance is the OPTIMIZER's own, not a number picked to make the test pass:
        // the last poll that improved nothing did so at a step s with tol < s <= 2*tol, and
        // it moved the answer both ways, so the optimum is within s of it.
        double tolerance = result.OptimumTolerance[0];
        Assert.Equal(2 * depth.StepTolerance, tolerance);

        double found = result.ValueOf(depth);
        double exact = AnalyticDepth(DeflectionLimit);
        Assert.Equal(15.618, exact, 3);                        // the fixture's own arithmetic
        Assert.InRange(found - exact, 0, tolerance);           // feasible side, within the criterion

        // The objective must be asserted WITH the parameter that produced it: a mass can be
        // right for the wrong depth.
        Assert.Equal(AnalyticMassGrams(found), result.Objective, 9);

        // The constraint is active at the answer, from the feasible side.
        var reading = result.ReadingOf("tip deflection");
        Assert.True(reading.Satisfied);
        Assert.InRange(reading.Value, DeflectionLimit * 0.999, DeflectionLimit);

        // What stopped the search, named.
        Assert.Equal(["tip deflection"], result.Stop.BindingConstraints);
        Assert.Empty(result.Stop.BindingBounds);
        Assert.Contains("tip deflection", result.Report());
    }

    [Fact]
    public void ATighterLimit_MovesTheAnswerToTheOtherAnalyticOptimum()
    {
        // The same study with a different limit must land on THAT limit's closed form —
        // one fixture agreeing with one number could be a coincidence; two cannot.
        const double limit = 0.4;
        var (part, extrude) = Cantilever(startDepth: 12);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(
            part, [depth], MassGrams,
            [StudyConstraint.AtMost("tip deflection", TipDeflection, limit)]);

        Assert.True(result.Feasible);
        Assert.InRange(result.ValueOf(depth) - AnalyticDepth(limit), 0, result.OptimumTolerance[0]);
        Assert.Equal(["tip deflection"], result.Stop.BindingConstraints);
    }

    // ---- a monotone objective rests on the box edge ------------------------------

    [Fact]
    public void AMonotoneObjective_RestsExactlyOnTheBoxEdge_AndNamesTheBound()
    {
        // Mass rises with depth and nothing holds it up, so the answer is the box's floor —
        // and the report has to SAY that is what stopped it.
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(part, [depth], MassGrams);

        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);
        Assert.True(result.Feasible);
        // Exact: the clamp assigns the bound verbatim, so a variable resting on one holds
        // its value bit for bit.
        Assert.Equal(5.0, result.ValueOf(depth));
        Assert.Equal(AnalyticMassGrams(5.0), result.Objective, 9);
        Assert.Empty(result.Stop.BindingConstraints);
        Assert.Single(result.Stop.BindingBounds);
        Assert.Contains("lower bound 5", result.Stop.BindingBounds[0]);
        Assert.Contains("Height", result.Stop.BindingBounds[0]);

        // The polls that would have gone below the floor are recorded, not silently dropped.
        Assert.Contains(result.Trajectory, s => s.Outcome == StudyPointOutcome.AtBound);
    }

    [Fact]
    public void AnInteriorOptimum_NamesNeitherAConstraintNorABound()
    {
        // Minimize |mass - 400 g|: the objective's own minimum sits inside the box, so the
        // stop report must say so rather than inventing a binding constraint.
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(part, [depth], p => Math.Abs(MassGrams(p) - 400));

        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);
        Assert.Empty(result.Stop.BindingConstraints);
        Assert.Empty(result.Stop.BindingBounds);
        Assert.Contains("interior optimum", result.Stop.Summary);

        // 400 g of steel at 200 x 20 is a depth of 400 / 31.4 mm.
        double wanted = 400 / AnalyticMassGrams(1);
        Assert.Equal(wanted, result.ValueOf(depth), 2);
    }

    // ---- two variables, with the answer on a constraint AND a bound ---------------

    /// <summary>A rectangular beam whose width and depth are both parameters.</summary>
    private sealed class BeamFeature : Feature
    {
        [Param(Min = 2, Max = 40, Description = "Section width, mm")]
        public double SectionWidth { get; init; } = 20;

        [Param(Min = 2, Max = 25, Description = "Section depth, mm")]
        public double SectionDepth { get; init; } = 20;

        public override Shape Apply(FeatureContext context) =>
            Shape.Extrude(Sketch.Rectangle(Length, SectionWidth), SectionDepth);
    }

    private static (Part Part, BeamFeature Beam, DesignVariable Width, DesignVariable Depth) Beam()
    {
        var beam = new BeamFeature { SectionWidth = 20, SectionDepth = 20 };
        var history = new FeatureHistory();
        history.Add(beam);
        return (history.ToPart("beam").Of(Materials.Steel), beam,
            DesignVariable.On(beam, nameof(BeamFeature.SectionWidth)),
            DesignVariable.On(beam, nameof(BeamFeature.SectionDepth)));
    }

    private static double BeamDeflection(Part part)
    {
        var size = part.Bounds().Size;
        double second = size.Y * size.Z * size.Z * size.Z / 12;
        return TipLoad * Length * Length * Length / (3 * Modulus * second);
    }

    [Fact]
    public void WithoutRatioAdaptation_TheSearchStopsOnTheConstraintBoundaryShortOfTheOptimum()
    {
        // The measurement that justifies the poll's step-RATIO adaptation, kept as a test so
        // the design decision cannot rot into prose. Halving every step together freezes the
        // diagonal directions' SLOPE, and the descent direction along this constraint is a
        // slope the model chooses — so every diagonal available is too steep and leaves the
        // feasible set, and the search stops at the first boundary point it reaches.
        var (part, _, width, depth) = Beam();

        var result = DesignStudy.Minimize(
            part, [width, depth], MassGrams,
            [StudyConstraint.AtMost("tip deflection", BeamDeflection, DeflectionLimit)],
            new StudyOptions { AdaptStepRatios = false });

        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);
        Assert.True(result.Feasible);                     // it is a legal design...
        Assert.Equal(21.920, result.ValueOf(depth), 3);   // ...and it is not the optimum
        Assert.True(result.ValueOf(depth) < 25);
        // What it DOES get right even then is the report: the deflection limit is what held
        // it, so the answer is never presented as an unconstrained minimum.
        Assert.Equal(["tip deflection"], result.Stop.BindingConstraints);
    }

    [Fact]
    public void RatioAdaptation_IsInertWhereNoConstraintHoldsTheAnswer()
    {
        // The other half of the same claim: the adaptation runs only when a constraint
        // refused an improving poll, so an unconstrained study polls bit-identically with it
        // on and off. A speed-up that quietly changed an answer would be worse than none.
        static StudyResult Run(bool adapt)
        {
            var (part, _, width, depth) = Beam();
            return DesignStudy.Minimize(
                part, [width, depth], MassGrams, null, new StudyOptions { AdaptStepRatios = adapt });
        }

        var with = Run(adapt: true);
        var without = Run(adapt: false);

        Assert.Equal(without.Trajectory.Count, with.Trajectory.Count);
        for (int i = 0; i < with.Trajectory.Count; i++)
        {
            for (int v = 0; v < with.Variables.Count; v++)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(without.Trajectory[i].Values[v]),
                    BitConverter.DoubleToInt64Bits(with.Trajectory[i].Values[v]));
            }
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(without.Trajectory[i].Objective),
                BitConverter.DoubleToInt64Bits(with.Trajectory[i].Objective));
        }
        // ...and the fixture really is one where the adaptation could have fired: both
        // variables move and the answer rests on two bounds.
        Assert.Equal(2, with.Stop.BindingBounds.Count);
    }

    [Fact]
    public void TwoVariables_SlideAlongTheActiveConstraint_ToTheAnalyticOptimum()
    {
        // Mass is proportional to w·d and the deflection limit demands w·d³ >= K, so the
        // lightest section is the DEEPEST one the box allows with the width that just meets
        // the limit — the answer sits on a constraint AND on a bound at once.
        //
        // This is also the case a plain compass poll cannot solve: on the active constraint
        // the descent direction is diagonal (deeper AND narrower), so every single-axis move
        // is either infeasible or heavier. It is what the poll's second stage is for.
        var (part, _, width, depth) = Beam();

        var result = DesignStudy.Minimize(
            part, [width, depth], MassGrams,
            [StudyConstraint.AtMost("tip deflection", BeamDeflection, DeflectionLimit)]);

        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);
        Assert.True(result.Feasible);

        // Depth pins to its ceiling; width follows from the closed form at that depth.
        Assert.Equal(25.0, result.ValueOf(depth));
        double exactWidth = 4 * TipLoad * Length * Length * Length
            / (Modulus * 25.0 * 25.0 * 25.0 * DeflectionLimit);
        Assert.Equal(4.876, exactWidth, 3);
        Assert.InRange(result.ValueOf(width) - exactWidth, 0, result.OptimumTolerance[0]);
        Assert.Equal(
            ModelUnits.MassToGrams(Length * result.ValueOf(width) * 25.0 * Materials.Steel.Density),
            result.Objective, 9);

        // Both halves of what stopped it are named.
        Assert.Equal(["tip deflection"], result.Stop.BindingConstraints);
        Assert.Single(result.Stop.BindingBounds);
        Assert.Contains("SectionDepth", result.Stop.BindingBounds[0]);
        Assert.Contains("upper bound 25", result.Stop.BindingBounds[0]);

        // And it is a genuine improvement on the start, not merely a legal design.
        Assert.True(result.Objective < ModelUnits.MassToGrams(
            Length * 20 * 20 * Materials.Steel.Density));
    }

    // ---- an unreachable limit ----------------------------------------------------

    [Fact]
    public void AnUnreachableLimit_IsReportedAsInfeasible_ByName()
    {
        // 1 micrometre of tip deflection needs a 156 mm deep beam; the box stops at 40.
        const double limit = 0.001;
        var (part, extrude) = Cantilever(startDepth: 20);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(
            part, [depth], MassGrams,
            [StudyConstraint.AtMost("tip deflection", TipDeflection, limit)]);

        Assert.True(result.Succeeded);           // the study ran; it just has no answer to give
        Assert.False(result.Feasible);
        Assert.Equal(StudyStopReason.NoFeasibleDesign, result.Stop.Reason);
        Assert.Equal(["tip deflection"], result.Stop.BindingConstraints);
        Assert.Contains("tip deflection", result.Stop.Summary);
        Assert.Contains("no design in the box meets every constraint", result.Stop.Summary);

        // Nothing in the box is feasible, so the search descends on VIOLATION and ends at the
        // deepest beam available — the least-bad design, reported as such rather than
        // returned quietly as if it were the answer.
        Assert.Equal(40.0, result.ValueOf(depth));
        Assert.True(AnalyticDepth(limit) > 40);
        Assert.False(result.ReadingOf("tip deflection").Satisfied);
        Assert.Contains("NO FEASIBLE DESIGN", result.Report());
    }

    // ---- refused designs ---------------------------------------------------------

    /// <summary>A two-bore link whose bore pitch is a parameter. <c>Shape.Drill</c> refuses
    /// overlapping or tangent holes AT THE CALL, so a pitch at or below the bore diameter is
    /// a genuine kernel refusal raised inside <c>Apply</c> — the study's
    /// <see cref="StudyPointOutcome.RegenerationFailed"/> case, with an analytically known
    /// boundary (centres must be more than one diameter apart).</summary>
    private sealed class TwoBoreLinkFeature : Feature
    {
        internal const double Bore = 8;
        private const double Margin = 5;
        private const double LinkWidth = 22;
        private const double Thickness = 6;

        [Param(Min = 5, Max = 60, Description = "Distance between the two bore centres")]
        public double Pitch { get; init; } = 40;

        public override Shape Apply(FeatureContext context) =>
            Shape.Extrude(Sketch.Rectangle(Pitch + Bore + 2 * Margin, LinkWidth), Thickness)
                .Drill(
                    HoleSpec.Simple(Bore),
                    [new Vector2d(-Pitch / 2, 0), new Vector2d(Pitch / 2, 0)],
                    Thickness * 1.1,
                    SketchPlane.At(new Vector3d(0, 0, Thickness), Vector3d.UnitX, Vector3d.UnitY));
    }

    [Fact]
    public void AKernelRefusal_IsRecordedInTheTrajectory_AndTheSearchFindsItsBoundary()
    {
        // "How short can this two-bore link be?" — the answer is set by the kernel's own
        // hole-overlap rule, which the study has to discover by being refused.
        var link = new TwoBoreLinkFeature { Pitch = 18 };
        var history = new FeatureHistory();
        history.Add(link);
        var part = history.ToPart("link").Of(Materials.Steel);
        var pitch = DesignVariable.On(
            link, nameof(TwoBoreLinkFeature.Pitch), min: 7, max: 20, stepTolerance: 0.05);

        var result = DesignStudy.Minimize(part, [pitch], MassGrams);

        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);
        Assert.True(result.RegenerationFailures > 0);

        // The refusals are DATA and they carry the kernel's own words.
        var refused = result.Trajectory
            .Where(s => s.Outcome == StudyPointOutcome.RegenerationFailed).ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, s => Assert.Contains("overlap or are tangent", s.Message));

        // The search continued past every one of them: an accepted point follows the first
        // refusal in the trajectory.
        int first = refused[0].Index;
        Assert.Contains(result.Trajectory.Skip(first + 1), s => s.Accepted);

        // And it converged onto the analytic boundary: the rule is "centres more than one
        // diameter apart", so the infimum is exactly the bore diameter, approached from the
        // buildable side and reached to the optimizer's own criterion.
        double found = result.ValueOf(pitch);
        Assert.InRange(found - TwoBoreLinkFeature.Bore, 0, result.OptimumTolerance[0]);

        // It got there by walking INTO the refusal rather than stopping short of it by luck:
        // the closest refused design sits below the answer, within the same final step.
        double closestRefused = refused.Max(s => s.Values[0]);
        Assert.True(closestRefused < found);
        Assert.InRange(found - closestRefused, 0, result.OptimumTolerance[0]);
    }

    [Fact]
    public void ALazyShapeRefusal_SurfacesAtMeasurementRatherThanAtRegeneration()
    {
        // The finding that shapes the study's two failure outcomes: a feature's Apply
        // usually only BUILDS a Shape node, so a geometric refusal is raised by the
        // lowering — i.e. the first time something measures the part — and a study that
        // only watched RegenerationResult.Succeeded would call this design fine.
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(30, 20)) { Height = 10 });
        history.Add(new FilletRimFeature { Radius = 11 });

        var regeneration = history.Regenerate();
        Assert.True(regeneration.Succeeded);

        var part = new Part("plate", regeneration.Body!);
        var refusal = Assert.ThrowsAny<Exception>(() => part.GetMesh());
        Assert.Contains("mitered corner offsets cross", refusal.Message);
    }

    [Fact]
    public void AMeasureThatRefuses_IsRecordedAndTheSearchContinuesFromTheLastGoodDesign()
    {
        // What an FEA-backed objective does on a mesh it will not accept. It is a different
        // outcome from a refused regeneration because it is a different failure, and both
        // have to leave the model where the incumbent was.
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(part, [depth], p =>
        {
            double d = p.Bounds().Size.Z;
            if (d < 12)
                throw new InvalidOperationException($"the analysis refused a {d:F3} mm section.");
            return MassGrams(p);
        });

        Assert.Equal(StudyStopReason.Converged, result.Stop.Reason);
        Assert.True(result.MeasurementFailures > 0);
        Assert.Equal(0, result.RegenerationFailures);
        Assert.Equal(result.RegenerationFailures + result.MeasurementFailures, result.RefusedDesigns);
        Assert.All(
            result.Trajectory.Where(s => s.Outcome == StudyPointOutcome.MeasurementFailed),
            s => Assert.Contains("the analysis refused", s.Message));

        // The measurable region is [12, 40] and mass is monotone, so the answer sits on the
        // measurable boundary — found without ever being told where it is.
        Assert.InRange(result.ValueOf(depth) - 12, 0, result.OptimumTolerance[0]);
    }

    [Fact]
    public void AStartingDesignThatDoesNotMeasure_IsReportedRatherThanSearchedFrom()
    {
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(
            part, [depth], _ => throw new InvalidOperationException("no analysis available"));

        Assert.False(result.Succeeded);
        Assert.Equal(StudyStopReason.StartFailed, result.Stop.Reason);
        Assert.Contains("no analysis available", result.Stop.Summary);
        Assert.Single(result.Trajectory);
        Assert.Equal(30.0, extrude.Height);          // untouched
    }

    // ---- determinism -------------------------------------------------------------

    [Fact]
    public void TheSameStudyTwice_ProducesAnIdenticalTrajectory()
    {
        // No RNG anywhere: the poll visits the variables in declaration order, plus before
        // minus, and takes the best improving direction of a COMPLETE poll. Asserted on the
        // whole trajectory rather than on the answer, because two searches can reach one
        // point by different routes and only the routes would show a difference.
        static StudyResult Run()
        {
            var (part, extrude) = Cantilever(startDepth: 30);
            var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);
            return DesignStudy.Minimize(
                part, [depth], MassGrams,
                [StudyConstraint.AtMost("tip deflection", TipDeflection, DeflectionLimit)]);
        }

        var first = Run();
        var second = Run();

        Assert.Equal(first.Evaluations, second.Evaluations);
        Assert.Equal(first.Trajectory.Count, second.Trajectory.Count);
        for (int i = 0; i < first.Trajectory.Count; i++)
        {
            var a = first.Trajectory[i];
            var b = second.Trajectory[i];
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Outcome, b.Outcome);
            Assert.Equal(a.Accepted, b.Accepted);
            Assert.Equal(a.Message, b.Message);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Step), BitConverter.DoubleToInt64Bits(b.Step));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.Objective), BitConverter.DoubleToInt64Bits(b.Objective));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(a.Violation), BitConverter.DoubleToInt64Bits(b.Violation));
            for (int v = 0; v < a.Values.Count; v++)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(a.Values[v]),
                    BitConverter.DoubleToInt64Bits(b.Values[v]));
            }
        }
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(first.Objective),
            BitConverter.DoubleToInt64Bits(second.Objective));
    }

    // ---- the study is an analysis, not an edit -----------------------------------

    [Fact]
    public void AStudyLeavesThePartAsItFoundIt_AndItsEditsApplyTheAnswer()
    {
        var (part, extrude) = Cantilever(startDepth: 30);
        double startMass = MassGrams(part);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(
            part, [depth], MassGrams,
            [StudyConstraint.AtMost("tip deflection", TipDeflection, DeflectionLimit)]);

        // A search evaluates dozens of designs and none of them is history: the part comes
        // back exactly as it went in, geometry included.
        Assert.Equal(30.0, extrude.Height);
        Assert.Equal(startMass, MassGrams(part), 9);
        Assert.Equal(30.0, part.Bounds().Size.Z, 9);

        // Adopting the answer is a deliberate, undoable act.
        var stack = new UndoStack();
        using (stack.Group("Apply study"))
        {
            foreach (var edit in result.Edits(part))
                stack.Do(edit);
        }
        Assert.Equal(result.ValueOf(depth), extrude.Height);
        Assert.Equal(result.Objective, MassGrams(part), 9);

        stack.Undo();
        Assert.Equal(30.0, extrude.Height);
        Assert.Equal(startMass, MassGrams(part), 9);
    }

    [Fact]
    public void TheAnswerSpellsItselfInTheSameJsonSeamAsASavedFile()
    {
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);
        var result = DesignStudy.Minimize(part, [depth], MassGrams);

        string? json = result.ValuesFor(extrude);
        Assert.NotNull(json);
        Assert.Contains("\"Height\"", json);
        // The very seam LoadParameters reads, so a study's answer cannot mean one thing here
        // and another in a saved file.
        var warnings = part.History!.LoadParameters($"{{\"{extrude.Name}\": {json}}}");
        Assert.Empty(warnings);
        Assert.Equal(result.ValueOf(depth), extrude.Height);
    }

    // ---- guards ------------------------------------------------------------------

    [Fact]
    public void ADiscreteParameter_IsRefusedByName()
    {
        var pattern = new LinearPatternFeature { Count = 3 };
        var exception = Assert.Throws<ArgumentException>(
            () => DesignVariable.On(pattern, nameof(LinearPatternFeature.Count)));
        Assert.Contains("continuous (double) parameters only", exception.Message);
    }

    [Fact]
    public void AParameterWithNoDeclaredBound_IsRefusedByName()
    {
        var fillet = new FilletRimFeature { Radius = 2 };   // [Param(Min = 1e-9)] — no Max
        var exception = Assert.Throws<ArgumentException>(
            () => DesignVariable.On(fillet, nameof(FilletRimFeature.Radius)));
        Assert.Contains("no finite upper bound", exception.Message);
        // ...and stating one is all it takes.
        Assert.Equal(12, DesignVariable.On(fillet, nameof(FilletRimFeature.Radius), max: 12).Max);
    }

    [Fact]
    public void WideningAParametersDeclaredBox_IsRefusedByName()
    {
        var extrude = new ExtrudeSketchFeature(Sketch.Rectangle(10, 10));
        var exception = Assert.Throws<ArgumentException>(
            () => DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: -5, max: 10));
        Assert.Contains("never widen it", exception.Message);
    }

    [Fact]
    public void AnUnknownParameter_NamesTheOnesThatExist()
    {
        var extrude = new ExtrudeSketchFeature(Sketch.Rectangle(10, 10));
        var exception = Assert.Throws<ArgumentException>(
            () => DesignVariable.On(extrude, "Thickness"));
        Assert.Contains("Height", exception.Message);
        Assert.Contains("Plane", exception.Message);
    }

    [Fact]
    public void AToleranceAtOrAboveTheInitialStep_IsRefusedByName()
    {
        var extrude = new ExtrudeSketchFeature(Sketch.Rectangle(10, 10));
        var exception = Assert.Throws<ArgumentException>(() => DesignVariable.On(
            extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40,
            initialStep: 0.001, stepTolerance: 0.01));
        Assert.Contains("before polling anything", exception.Message);
    }

    [Fact]
    public void AVariableOnAFeatureOutsideThePart_IsRefusedByName()
    {
        var (part, _) = Cantilever(startDepth: 30);
        var stranger = new ExtrudeSketchFeature(Sketch.Rectangle(10, 10));
        var variable = DesignVariable.On(stranger, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var exception = Assert.Throws<ArgumentException>(
            () => DesignStudy.Minimize(part, [variable], MassGrams));
        Assert.Contains("not in part 'beam''s history", exception.Message);
    }

    [Fact]
    public void OneParameterDeclaredTwice_IsRefusedByName()
    {
        var (part, extrude) = Cantilever(startDepth: 30);
        var a = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);
        var b = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 6, max: 30);

        var exception = Assert.Throws<ArgumentException>(
            () => DesignStudy.Minimize(part, [a, b], MassGrams));
        Assert.Contains("declared twice", exception.Message);
    }

    [Fact]
    public void AStartOutsideItsOwnStudyBox_IsRefusedByName()
    {
        var (part, extrude) = Cantilever(startDepth: 50);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var exception = Assert.Throws<ArgumentException>(
            () => DesignStudy.Minimize(part, [depth], MassGrams));
        Assert.Contains("outside its own study box", exception.Message);
    }

    [Fact]
    public void APartWithNoHistory_IsRefusedByName()
    {
        var part = new Part("block", Shape.Box(10, 10, 10));
        var extrude = new ExtrudeSketchFeature(Sketch.Rectangle(10, 10));
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var exception = Assert.Throws<ArgumentException>(
            () => DesignStudy.Minimize(part, [depth], MassGrams));
        Assert.Contains("no feature history to drive", exception.Message);
    }

    [Fact]
    public void AnObjectiveThatReturnsNaN_IsRefusedByNameRatherThanWinningEveryComparison()
    {
        // NaN loses every comparison, so an unguarded study would treat it as "not better"
        // and quietly keep searching around a point that measured nothing.
        var (part, extrude) = Cantilever(startDepth: 30);
        var depth = DesignVariable.On(extrude, nameof(ExtrudeSketchFeature.Height), min: 5, max: 40);

        var result = DesignStudy.Minimize(part, [depth], _ => double.NaN);

        Assert.False(result.Succeeded);
        Assert.Equal(StudyStopReason.StartFailed, result.Stop.Reason);
        Assert.Contains("returned NaN", result.Stop.Summary);
    }

    // ---- constraint arithmetic ---------------------------------------------------

    [Fact]
    public void ConstraintMarginsAreRelativeToTheirOwnLimit_SoTwoUnitsCanBeCompared()
    {
        // A stress cap in MPa and a deflection cap in millimetres have to be ranked somehow;
        // a ratio to each constraint's own limit is the only dimensionless choice that needs
        // no weight from the caller.
        var stress = StudyConstraint.AtMost("stress", _ => 0, 250);
        var deflection = StudyConstraint.AtMost("deflection", _ => 0, 0.5);
        Assert.Equal(0.10, stress.MarginOf(225), 12);
        Assert.Equal(0.10, deflection.MarginOf(0.45), 12);
        Assert.Equal(0.10, stress.ViolationOf(275), 12);
        Assert.Equal(0, stress.ViolationOf(250));            // the boundary is legal

        var frequency = StudyConstraint.AtLeast("frequency", _ => 0, 120);
        Assert.Equal(0.10, frequency.MarginOf(132), 12);
        Assert.Equal(0.10, frequency.ViolationOf(108), 12);

        // A limit of exactly zero has no scale, so violations against it are absolute.
        var gap = StudyConstraint.AtLeast("gap", _ => 0, 0);
        Assert.Equal(2.0, gap.ViolationOf(-2), 12);
    }
}
