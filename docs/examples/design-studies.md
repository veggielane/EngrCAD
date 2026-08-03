# Design studies

A design study drives a part's `[Param]` values by an optimizer against a **measured**
objective: minimize mass subject to a deflection limit, find the largest fillet the
kernel will build, size a section to a stiffness floor.

Everything under the loop already exists — [`FeatureHistory`](features.md) regenerates
with prefix caching, `[Param(Min =, Max =)]` already declares the box a search may move
in, and any measurement you can write over a `Part` is an objective. **The study is the
loop.**

```csharp run:design-study-cantilever
// A 200 mm cantilever, 20 mm wide, carrying 500 N at the tip. How shallow can the
// section be and still deflect no more than 1 mm?
sealed class Beam : Feature
{
    [Param(Min = 2, Max = 25, Units = "mm")] public double SectionDepth { get; init; } = 20;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.Rectangle(200, 20), SectionDepth);
}

var beam = new Beam();
var history = new FeatureHistory();
history.Add(beam);
var part = history.ToPart("beam").Of(Materials.Steel);

// Both measures read the REGENERATED part, not the parameter that was written into it.
double Mass(Part p) => p.MassGrams()!.Value;
double TipDeflection(Part p)
{
    var size = p.Bounds().Size;                       // the section, measured
    double second = size.Y * size.Z * size.Z * size.Z / 12;
    return 500 * 200.0 * 200 * 200 / (3 * Materials.Steel.YoungsModulus * second);
}

var depth = DesignVariable.On(beam, nameof(Beam.SectionDepth));
var result = DesignStudy.Minimize(
    part, [depth], Mass,
    [StudyConstraint.AtMost("tip deflection", TipDeflection, 1.0)]);

Console.WriteLine(result.Report());

// The analytic answer: delta = 4PL^3/(E b d^3), so the lightest beam meeting the limit
// is d* = cbrt(4PL^3/(E b delta)) = 15.6179 mm.
double exact = Math.Cbrt(4 * 500 * 200.0 * 200 * 200 / (Materials.Steel.YoungsModulus * 20 * 1.0));
double found = result.ValueOf(depth);
if (!result.Feasible || found < exact || found - exact > result.OptimumTolerance[0])
    throw new Exception($"expected {exact:F4} mm within {result.OptimumTolerance[0]:F4}, got {found:F4}");
if (result.Stop.BindingConstraints is not ["tip deflection"])
    throw new Exception("the deflection limit should be what stopped the search");
```

```text
Design study: feasible optimum
  objective          490.472
  Beam.SectionDepth  15.6201   in [2, 25]
  tip deflection     0.999581 <= 1   margin 0.04%
  stop               Converged: held by tip deflection
  evaluations        35
```

The answer is 15.6201 mm against the closed form's **15.6179 mm** — inside the
optimizer's own tolerance, and *above* it, because the search never leaves the feasible
side. A study that said "15.62 mm" without saying what stopped it would not be usable
engineering output, so the report names the binding constraint beside the answer.

## What the pieces are

| Type | What it is |
| --- | --- |
| `DesignVariable.On(feature, parameter, …)` | One `[Param]` the search may move, with the box it may move it in. |
| `Func<Part, double>` | The objective. Mass, deflection, a frequency, a compound — anything you can measure on a regenerated part. |
| `StudyConstraint.AtMost` / `.AtLeast` | A limit the answer must meet, measured the same way. |
| `StudyResult` | The design, the objective, every constraint's reading, the trajectory, and what stopped the search. |

The box comes from the feature's **own** declaration — `[Param(Min =, Max =)]` is where a
feature already states what values it accepts. A study may narrow it; widening is refused
by name, since the regeneration would reject the value anyway. A parameter with no finite
bound on the side you did not supply is refused too: a pattern search needs a box.

Only `double` parameters in v1. An `int` `[Param]` (a pattern count, a tooth number) is a
discrete variable — the step may not halve below one and the stopping rule means something
different — so it is refused by name rather than rounded silently.

## Which optimizer, and why

A **Hooke–Jeeves pattern search**: poll each variable ± a step, take the best improving
direction, extrapolate along it, halve the step when nothing improves.

It is derivative-free **by necessity**. A regeneration is not differentiable: a parameter
change can alter *topology* — a hole breaks through, a fillet stops fitting — so a
finite-difference gradient across such a step is meaningless rather than merely noisy.

Within the derivative-free family, a pattern search beats Nelder–Mead here for three
reasons that are about this problem rather than about convergence rates:

- **The box is the point.** A simplex has no natural way to rest *on* a bound, and
  clamping collapses it. A compass poll clamps to the bound and keeps polling.
- **A refused design is just a poll that does not improve.** A simplex vertex that cannot
  be evaluated has to be given a fictitious value, and can degenerate the simplex.
- **The step size *is* a distance in parameter space**, so the stopping rule states a bound
  on the answer. A simplex diameter does not.

That bound is `StudyResult.OptimumTolerance`, and it is the optimizer's own criterion
rather than a number chosen to look good. The search halves every step together, so the
last poll that improved nothing did so at a step `s` with `tol < s <= 2·tol`; at that poll
it moved the answer by ±`s` along each axis and neither direction was better. For an
objective that is unimodal along the axis, the optimum is therefore within `2·tol` of the
answer. A search stopped by its **evaluation budget** makes no such claim, and
`OptimumTolerance` returns infinity to say so.

## Constraints are a filter, not a penalty

A penalty needs a weight, and a weight silently trades one gram against one micrometre — a
number nobody can justify. Worse, a penalized search can **return** an infeasible design
that merely scored well.

So constraints are a *feasibility filter*: points are compared lexicographically as
`(violation, objective)`, a feasible point always beats an infeasible one, and the answer
meets its limits or the study says it could not. While no feasible point is known the
search descends on violation alone, which is what gets it into the feasible region from an
infeasible start.

Violation and margin are relative to each constraint's **own limit**. That is what lets a
stress cap in MPa and a deflection cap in millimetres be ranked at all: a ratio is the only
dimensionless choice that needs no weight from the caller.

## What stopped the search

`StudyStop` reports two things, and both are measurements rather than judgements:

- **A binding bound** — a variable resting exactly on `Min` or `Max` at the answer. The
  clamp assigns the bound verbatim, so the comparison is exact and needs no epsilon.
- **A binding constraint** — one that, in the *final poll round*, refused a neighbouring
  design that would have improved the objective. That is the definition of "this is what
  stopped the search", read off the search's own last act rather than inferred from a
  tolerance on a margin.

```csharp run:design-study-two-variables
// The same beam with the width free as well. Mass goes as w*d and the limit demands
// w*d^3 >= K, so the lightest section is the DEEPEST the box allows, with the width that
// just meets the limit: the answer sits on a constraint AND on a bound at once.
sealed class Beam2 : Feature
{
    [Param(Min = 2, Max = 40, Units = "mm")] public double SectionWidth { get; init; } = 20;
    [Param(Min = 2, Max = 25, Units = "mm")] public double SectionDepth { get; init; } = 20;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.Rectangle(200, SectionWidth), SectionDepth);
}

var beam = new Beam2();
var history = new FeatureHistory();
history.Add(beam);
var part = history.ToPart("beam").Of(Materials.Steel);

double Deflection(Part p)
{
    var size = p.Bounds().Size;
    return 500 * 200.0 * 200 * 200
        / (3 * Materials.Steel.YoungsModulus * (size.Y * size.Z * size.Z * size.Z / 12));
}

var width = DesignVariable.On(beam, nameof(Beam2.SectionWidth));
var depth = DesignVariable.On(beam, nameof(Beam2.SectionDepth));
var result = DesignStudy.Minimize(
    part, [width, depth], p => p.MassGrams()!.Value,
    [StudyConstraint.AtMost("tip deflection", Deflection, 1.0)]);

Console.WriteLine(result.Report());

if (result.ValueOf(depth) != 25)
    throw new Exception("the depth should rest on its ceiling");
double exactWidth = 4 * 500 * 200.0 * 200 * 200
    / (Materials.Steel.YoungsModulus * 25 * 25 * 25 * 1.0);      // 4.876 mm
if (Math.Abs(result.ValueOf(width) - exactWidth) > result.OptimumTolerance[0])
    throw new Exception($"expected a width of {exactWidth:F4} mm");
if (result.Stop.BindingConstraints.Count != 1 || result.Stop.BindingBounds.Count != 1)
    throw new Exception("both halves of what stopped it should be named");
```

```text
Design study: feasible optimum
  objective           191.39
  Beam2.SectionWidth  4.87619   in [2, 40]
  Beam2.SectionDepth  25   in [2, 25]
  tip deflection      1 <= 1   margin 0.00%
  stop                Converged: held by tip deflection, with Beam2.SectionDepth at its upper bound 25
  evaluations         496
```

That case is harder than it looks, and the way it is solved is worth knowing about.
On an **active** constraint coupling two variables, the descent direction runs *along* the
boundary — deeper *and* narrower — so no single-axis move helps. The poll therefore adds
the pairwise diagonals `±e_i ± e_j` when the axis poll finds nothing. But a diagonal's
*slope* is the ratio of the two steps, and halving every step together leaves that slope
unchanged forever, so it is either always too steep or always too shallow: measured, this
beam stopped at a depth of **21.92** against its analytic ceiling of 25. The fix is to
adapt the ratio — halve **one** axis's step at a time — and it runs only when a constraint
refused a poll that would have improved the objective, which is the only situation the
ratio can matter in. An unconstrained study, and a single-variable study of any kind, poll
bit-identically without it.

The direction set is still finite, so this is not a completeness claim: a boundary whose
slope never lands between two reachable ones can still stop the search. The report names
the constraint that held it either way, so the answer is never presented as an
unconstrained minimum.

## Designs the model refuses

A study walks into geometry that will not build — that is *how* it finds a boundary. A
refused design is **data**, not an exception: it is recorded in the trajectory with the
kernel's own message and the search continues from the last good design.

```csharp run:design-study-refusals
// A two-bore link. Shape.Drill refuses overlapping or tangent holes, so "how short can
// this link be?" is answered by the kernel refusing.
sealed class Link : Feature
{
    [Param(Min = 7, Max = 20, Units = "mm")] public double Pitch { get; init; } = 18;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.Rectangle(Pitch + 18, 22), 6)
            .Drill(
                StandardHoles.Clearance(8), [new Vector2d(-Pitch / 2, 0), new Vector2d(Pitch / 2, 0)],
                depth: 6.6, SketchPlane.At(new Vector3d(0, 0, 6), Vector3d.UnitX, Vector3d.UnitY));
}

var link = new Link();
var history = new FeatureHistory();
history.Add(link);
var part = history.ToPart("link").Of(Materials.Steel);

var pitch = DesignVariable.On(link, nameof(Link.Pitch), stepTolerance: 0.05);
var result = DesignStudy.Minimize(part, [pitch], p => p.MassGrams()!.Value);

var refused = result.Trajectory
    .Where(s => s.Outcome == StudyPointOutcome.RegenerationFailed).ToList();
Console.WriteLine($"{result.Evaluations} designs, {refused.Count} refused by the kernel");
Console.WriteLine($"  e.g. {refused[0].Message}");
Console.WriteLine($"  shortest buildable pitch: {result.ValueOf(pitch):F3} mm");

if (refused.Count == 0)
    throw new Exception("the search should have been refused on its way down");
if (refused.Any(s => s.Values[0] >= result.ValueOf(pitch)))
    throw new Exception("every refused design should sit below the answer");
```

```text
28 designs, 9 refused by the kernel
  e.g. Link: ArgumentException: Holes at (-4.125, 0) and (4.125, 0) (surface diameter 9 each)
       overlap or are tangent; centers must be more than 9 apart. (Parameter 'points')
  shortest buildable pitch: 9.012 mm
```

The answer is the kernel's own rule, discovered rather than told: an M8 clearance bore is
9 mm across, centres must be *more* than one diameter apart, and the study converges onto
9 mm from the buildable side within its own tolerance.

There are two outcomes for a design the model will not accept, and the difference is worth
knowing:

- **`RegenerationFailed`** — a feature refused outright, as `Shape.Drill` does above.
- **`MeasurementFailed`** — the history regenerated but the objective or a constraint could
  not produce a finite number. **Most geometric refusals land here**, because a `Shape`
  graph is *lazy*: a feature's `Apply` usually only builds a graph node, so a kernel
  refusal ("the rim feature consumes the edge … its mitered corner offsets cross") is
  raised by the *lowering* — which happens the first time something measures the part. A
  study that watched only `RegenerationResult.Succeeded` would call that design fine.

Either way the study takes the parameter back and rebuilds the incumbent before moving on:
`Part.Regenerate` keeps the previous *body* on failure but the bad *parameter* is still
set, so a loop that skipped the restore would measure its next point on a poisoned state.

## When there is no answer

A limit no design in the box can meet is reported by name, not returned quietly as the
least-bad point:

```csharp run:design-study-infeasible
sealed class Beam3 : Feature
{
    [Param(Min = 2, Max = 25, Units = "mm")] public double SectionDepth { get; init; } = 20;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.Rectangle(200, 20), SectionDepth);
}

var beam = new Beam3();
var history = new FeatureHistory();
history.Add(beam);
var part = history.ToPart("beam").Of(Materials.Steel);

double Deflection(Part p)
{
    var size = p.Bounds().Size;
    return 500 * 200.0 * 200 * 200
        / (3 * Materials.Steel.YoungsModulus * (size.Y * size.Z * size.Z * size.Z / 12));
}

var depth = DesignVariable.On(beam, nameof(Beam3.SectionDepth));
var result = DesignStudy.Minimize(
    part, [depth], p => p.MassGrams()!.Value,
    [StudyConstraint.AtMost("tip deflection", Deflection, 0.001)]);   // 1 micrometre

Console.WriteLine(result.Report());

if (result.Feasible || result.Stop.Reason != StudyStopReason.NoFeasibleDesign)
    throw new Exception("a micrometre of tip deflection is not reachable in this box");
if (result.ValueOf(depth) != 25)
    throw new Exception("the least-violating design is the deepest section available");
```

The other stop reasons are `Converged`, `EvaluationBudget` (the answer is the best point
seen and carries **no** accuracy claim), `StartFailed` (the starting design itself would
not build or would not measure) and `Cancelled`.

## Determinism, and what a study does *not* do

There is no randomness anywhere: the poll visits the variables in declaration order, plus
before minus, and takes the best improving direction of a **complete** poll. Two runs of
one study produce identical trajectories, asserted bit for bit — on the whole trajectory,
because two searches can reach one point by different routes and only the routes would
show a difference.

**A study is an analysis, not an edit.** It restores the part to the values it started from
and hands the answer back as data; a search evaluates hundreds of designs and none of them
is history. Adopting the answer is a deliberate, undoable act:

```csharp run:design-study-apply
sealed class Plate : Feature
{
    [Param(Min = 4, Max = 20, Units = "mm")] public double Thickness { get; init; } = 12;

    public override Shape Apply(FeatureContext c) =>
        Shape.Extrude(Sketch.Rectangle(60, 40), Thickness);
}

var plate = new Plate();
var history = new FeatureHistory();
history.Add(plate);
var part = history.ToPart("plate").Of(Materials.Aluminium6061);

var thickness = DesignVariable.On(plate, nameof(Plate.Thickness));
var result = DesignStudy.Minimize(
    part, [thickness], p => p.MassGrams()!.Value,
    [StudyConstraint.AtLeast("section modulus", p => 60 * Math.Pow(p.Bounds().Size.Z, 2) / 6, 500)]);

if (plate.Thickness != 12)
    throw new Exception("the study should have left the part exactly as it found it");

var undo = new UndoStack();
using (undo.Group("Apply study"))
{
    foreach (var edit in result.Edits(part))
        undo.Do(edit);
}
Console.WriteLine($"applied: {plate.Thickness:F4} mm");

// 60*t^2/6 >= 500 means t >= sqrt(50) = 7.0711 mm — another closed form to land on.
if (Math.Abs(plate.Thickness - Math.Sqrt(50)) > result.OptimumTolerance[0])
    throw new Exception($"expected {Math.Sqrt(50):F4} mm, got {plate.Thickness:F4}");

undo.Undo();                                  // one step takes the whole thing back
if (plate.Thickness != 12)
    throw new Exception("undo should restore the original design");
```

`StudyResult.Edits(part)` builds one `SetParameters` edit per driven feature through the
same JSON seam as [`SaveParameters`](features.md#json-parameters) and the MCP server's
`set_param`, so a study's answer cannot mean one thing here and another in a saved file.
`StudyResult.ValuesFor(feature)` is the raw JSON if you would rather write it yourself.

## The trajectory

`StudyResult.Trajectory` is every design point in the order it was visited — the values,
what happened there, the objective, every constraint's reading, whether it became the
incumbent, and the kernel's message when it did not build. It is a deliverable rather than
a log: it is what makes a refused design visible, and comparing two runs' trajectories is
how the study's determinism is asserted.

## Cost

Every point is a full regeneration plus whatever the measures cost, and nothing is
memoized — the trajectory is exactly the list of evaluations performed, which is what makes
the determinism comparison mean what it says. Budget accordingly: the one-variable
cantilever above takes 35 regenerations, the two-variable one 496 (the ratio adaptation
re-polls). `StudyOptions.MaxEvaluations` caps it, and `DesignStudy.Minimize` takes an
optional `ProgressCancel` whose reported fraction is the share of the step-halving schedule
completed — the only honest fraction available, since how many poll rounds a given step
size takes is not known in advance.
