using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Which side of its limit a <see cref="StudyConstraint"/> must stay on.</summary>
public enum StudySense
{
    /// <summary>The measured value must not exceed the limit (a stress or deflection cap).</summary>
    AtMost,

    /// <summary>The measured value must reach the limit (a stiffness or frequency floor).</summary>
    AtLeast,
}

/// <summary>What a study did at one design point.</summary>
public enum StudyPointOutcome
{
    /// <summary>The model rebuilt and every measure answered.</summary>
    Evaluated,

    /// <summary>A feature refused this design: <see cref="FeatureHistory.Regenerate"/>
    /// reported a failure. The point is <em>data</em> — it bounds the buildable region —
    /// not an error: the study takes the value back, rebuilds the incumbent, and carries on.</summary>
    RegenerationFailed,

    /// <summary>
    /// The history regenerated but the objective or a constraint could not produce a finite
    /// number.
    /// <para><b>This is where most GEOMETRIC refusals land, and the reason is worth knowing:
    /// a <see cref="Shape"/> graph is lazy.</b> A feature's <c>Apply</c> usually only builds
    /// a graph node, so a kernel refusal ("the rim feature consumes the edge … its mitered
    /// corner offsets cross") is raised by the LOWERING, which happens the first time
    /// something measures the part. The study does not force a lowering of its own, because
    /// only the caller's measures know which representation they need; the kernel's own
    /// message is carried verbatim in <see cref="StudyStep.Message"/>.</para>
    /// </summary>
    MeasurementFailed,

    /// <summary>Not evaluated: the poll would have left the box, and the variable is
    /// already at that bound, so the clamped point is the incumbent itself.</summary>
    AtBound,
}

/// <summary>Why a design point was visited.</summary>
public enum StudyStepKind
{
    /// <summary>The starting design.</summary>
    Start,

    /// <summary>One direction of an exploratory poll.</summary>
    Poll,

    /// <summary>A pattern (acceleration) move along the direction the incumbent last
    /// travelled.</summary>
    Pattern,
}

/// <summary>Why a study stopped.</summary>
public enum StudyStopReason
{
    /// <summary>Every step fell below its tolerance — see
    /// <see cref="StudyResult.OptimumTolerance"/> for what that guarantees.</summary>
    Converged,

    /// <summary>The evaluation budget ran out first. The answer is the best point seen,
    /// and <b>no tolerance claim holds</b>.</summary>
    EvaluationBudget,

    /// <summary>No point in the box met every constraint. The answer is the point with
    /// the least total violation, and <see cref="StudyStop.Summary"/> names the
    /// constraints that could not be met and by how much.</summary>
    NoFeasibleDesign,

    /// <summary>The starting design itself would not rebuild (or would not measure), so
    /// there was nothing to search from.</summary>
    StartFailed,

    /// <summary>The caller cancelled through its <see cref="ProgressCancel"/>.</summary>
    Cancelled,
}

/// <summary>
/// One <c>[Param]</c> an optimizer is allowed to move, with the box it may move it in.
///
/// <para>The box is the feature's own declaration — <c>[Param(Min =, Max =)]</c> — because
/// that is where a feature already states what values it accepts, and a search that left it
/// would only be told so by a failed regeneration. A caller may NARROW the box; widening it
/// is refused, since the regeneration would reject the value anyway.</para>
///
/// <para><b>Continuous variables only in v1.</b> An <c>int</c> <c>[Param]</c> (a pattern
/// count, a tooth number) is a discrete variable: the step may not halve below one and the
/// convergence criterion means something different. It is refused by name rather than
/// rounded silently.</para>
/// </summary>
public sealed class DesignVariable
{
    private DesignVariable(
        Feature feature, string parameter, double min, double max, double initialStep, double stepTolerance)
    {
        Feature = feature;
        Parameter = parameter;
        Min = min;
        Max = max;
        InitialStep = initialStep;
        StepTolerance = stepTolerance;
    }

    /// <summary>The feature carrying the parameter.</summary>
    public Feature Feature { get; }

    /// <summary>The <c>[Param]</c> property name.</summary>
    public string Parameter { get; }

    /// <summary>Lower bound (inclusive).</summary>
    public double Min { get; }

    /// <summary>Upper bound (inclusive).</summary>
    public double Max { get; }

    /// <summary>The poll distance the search starts from; it halves on every failed poll.</summary>
    public double InitialStep { get; }

    /// <summary>The search stops once the step falls below this. See
    /// <see cref="StudyResult.OptimumTolerance"/> for the guarantee it buys.</summary>
    public double StepTolerance { get; }

    /// <summary>"FeatureName.Parameter" — how the variable is named in a report.</summary>
    public string Name => $"{Feature.Name}.{Parameter}";

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} in [{Min.ToString("G6", CultureInfo.InvariantCulture)}, "
        + $"{Max.ToString("G6", CultureInfo.InvariantCulture)}]";

    /// <summary>
    /// Declares a design variable over one of <paramref name="feature"/>'s <c>[Param]</c>
    /// properties.
    /// </summary>
    /// <param name="feature">The feature to drive; it must belong to the studied part's history.</param>
    /// <param name="parameter">The <c>[Param]</c> property name (a <c>double</c> in v1).</param>
    /// <param name="min">Narrows the declared lower bound; null takes <c>[Param(Min =)]</c>.</param>
    /// <param name="max">Narrows the declared upper bound; null takes <c>[Param(Max =)]</c>.</param>
    /// <param name="initialStep">First poll distance; null takes an eighth of the range —
    /// a bounded box has a natural scale, and eighths leave three halvings before the
    /// search is working at a percent of the range.</param>
    /// <param name="stepTolerance">Convergence threshold on the step; null takes 1e-4 of
    /// the range.</param>
    /// <exception cref="ArgumentException">The parameter does not exist, is not a
    /// <c>double</c>, has no finite bound on a side the caller did not supply, or the
    /// requested box lies outside the declared one.</exception>
    public static DesignVariable On(
        Feature feature,
        string parameter,
        double? min = null,
        double? max = null,
        double? initialStep = null,
        double? stepTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentException.ThrowIfNullOrEmpty(parameter);

        var info = feature.Parameters.FirstOrDefault(p => p.Name == parameter)
            ?? throw new ArgumentException(
                $"{feature.GetType().Name} has no [Param] named '{parameter}' "
                + $"(it has {Listed(feature.Parameters.Select(p => p.Name))}).",
                nameof(parameter));

        if (info.Type != typeof(double))
        {
            throw new ArgumentException(
                $"{feature.Name}.{parameter} is a {info.Type.Name}; a design study drives "
                + "continuous (double) parameters only. A discrete parameter needs an integer "
                + "search, which this study does not do.",
                nameof(parameter));
        }

        double low = min ?? info.Min;
        double high = max ?? info.Max;
        if (!double.IsFinite(low) || !double.IsFinite(high))
        {
            throw new ArgumentException(
                $"{feature.Name}.{parameter} has no finite {(double.IsFinite(low) ? "upper" : "lower")} "
                + "bound: declare one with [Param(Min = , Max = )] or pass it here. A pattern "
                + "search needs a box to search in.",
                nameof(parameter));
        }
        if (low >= high)
        {
            throw new ArgumentException(
                $"{feature.Name}.{parameter}: the box [{low}, {high}] is empty.", nameof(parameter));
        }
        if (low < info.Min || high > info.Max)
        {
            throw new ArgumentException(
                $"{feature.Name}.{parameter}: the requested box [{low}, {high}] reaches outside "
                + $"the declared [{info.Min}, {info.Max}]. A study may narrow a parameter's "
                + "declared range, never widen it — the regeneration would refuse the value.",
                nameof(parameter));
        }

        double range = high - low;
        double step = initialStep ?? range / 8.0;
        double tolerance = stepTolerance ?? range * 1e-4;
        if (!(step > 0) || !double.IsFinite(step))
            throw new ArgumentException($"{feature.Name}.{parameter}: the initial step must be positive.", nameof(initialStep));
        if (!(tolerance > 0) || !double.IsFinite(tolerance))
            throw new ArgumentException($"{feature.Name}.{parameter}: the step tolerance must be positive.", nameof(stepTolerance));
        if (step <= tolerance)
        {
            throw new ArgumentException(
                $"{feature.Name}.{parameter}: the initial step ({step}) is already at or below "
                + $"the tolerance ({tolerance}), so the search would stop before polling anything.",
                nameof(initialStep));
        }

        return new DesignVariable(feature, parameter, low, high, step, tolerance);
    }

    internal double Current() =>
        (double)(Feature.Parameters.First(p => p.Name == Parameter).Value
            ?? throw new InvalidOperationException($"{Name} has no value."));

    internal double Clamp(double value) => Math.Clamp(value, Min, Max);

    private static string Listed(IEnumerable<string> names)
    {
        var list = names.Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }
}

/// <summary>
/// A limit a design must meet, measured on the regenerated part.
///
/// <para>Violation and margin are reported <b>relative to the limit</b>, which is what lets
/// a stress cap in MPa and a deflection cap in millimetres be compared at all: a study with
/// two constraints in different units has to rank them somehow, and a ratio to each
/// constraint's own limit is the only dimensionless choice that needs no weight from the
/// caller. (A limit of exactly zero has no scale, so violations against it are absolute.)</para>
/// </summary>
public sealed class StudyConstraint
{
    private StudyConstraint(string name, Func<Part, double> measure, double limit, StudySense sense)
    {
        Name = name;
        Measure = measure;
        Limit = limit;
        Sense = sense;
    }

    /// <summary>What the constraint is called in a report.</summary>
    public string Name { get; }

    /// <summary>Measures the constrained quantity on the regenerated part.</summary>
    public Func<Part, double> Measure { get; }

    /// <summary>The limit itself.</summary>
    public double Limit { get; }

    /// <summary>Which side of <see cref="Limit"/> the measured value must stay on.</summary>
    public StudySense Sense { get; }

    /// <summary>"the measured value must not exceed <paramref name="limit"/>".</summary>
    public static StudyConstraint AtMost(string name, Func<Part, double> measure, double limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(measure);
        return new StudyConstraint(name, measure, limit, StudySense.AtMost);
    }

    /// <summary>"the measured value must reach <paramref name="limit"/>".</summary>
    public static StudyConstraint AtLeast(string name, Func<Part, double> measure, double limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(measure);
        return new StudyConstraint(name, measure, limit, StudySense.AtLeast);
    }

    /// <summary>How far past the limit a value is, as a fraction of the limit; 0 when satisfied.</summary>
    internal double ViolationOf(double value) => Math.Max(0, -MarginOf(value));

    /// <summary>Slack as a fraction of the limit: positive inside the limit, negative past it.</summary>
    internal double MarginOf(double value)
    {
        double scale = Limit == 0 ? 1 : Math.Abs(Limit);
        double past = Sense == StudySense.AtMost ? value - Limit : Limit - value;
        return -past / scale;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} {(Sense == StudySense.AtMost ? "<=" : ">=")} {Limit.ToString("G6", CultureInfo.InvariantCulture)}";
}

/// <summary>One constraint's reading at a design point.</summary>
/// <param name="Name">The constraint's name.</param>
/// <param name="Value">What the measure returned.</param>
/// <param name="Limit">The limit.</param>
/// <param name="Sense">Which side of the limit is legal.</param>
/// <param name="Margin">Relative slack: positive inside the limit, negative past it.</param>
public readonly record struct ConstraintReading(
    string Name, double Value, double Limit, StudySense Sense, double Margin)
{
    /// <summary>True when the value is on the legal side of the limit (the boundary counts).</summary>
    public bool Satisfied => Margin >= 0;
}

/// <summary>One variable's value at a design point.</summary>
/// <param name="Variable">The variable.</param>
/// <param name="Value">Its value.</param>
public readonly record struct DesignValue(DesignVariable Variable, double Value);

/// <summary>
/// One design point the study visited, in the order it was visited. The trajectory is a
/// deliverable, not a log: it is what makes a refused regeneration visible, and comparing
/// two runs' trajectories is how the study's determinism is asserted.
/// </summary>
/// <param name="Index">Position in the trajectory, from 0.</param>
/// <param name="Kind">Why the point was visited.</param>
/// <param name="Variable">The variable a poll moved — the FIRST of them for a diagonal
/// poll, which moves two; null for the start and for pattern moves.</param>
/// <param name="Step">The signed distance a poll moved it (0 otherwise).</param>
/// <param name="Values">The whole design vector, aligned with <see cref="StudyResult.Variables"/>.</param>
/// <param name="Outcome">What happened here.</param>
/// <param name="Objective">The objective value; NaN when it could not be measured.</param>
/// <param name="Violation">Total relative constraint violation; 0 when feasible, NaN when unmeasured.</param>
/// <param name="Constraints">Per-constraint readings (empty when the point did not measure).</param>
/// <param name="Accepted">True when this point became the incumbent.</param>
/// <param name="Message">Why the point failed, when it did.</param>
public sealed record StudyStep(
    int Index,
    StudyStepKind Kind,
    DesignVariable? Variable,
    double Step,
    IReadOnlyList<double> Values,
    StudyPointOutcome Outcome,
    double Objective,
    double Violation,
    IReadOnlyList<ConstraintReading> Constraints,
    bool Accepted,
    string? Message);

/// <summary>
/// Why a study stopped, and — the part an engineer actually needs — <b>what stopped it</b>.
///
/// <para>Both lists are measurements rather than judgements. A bound is binding when the
/// answer's value is exactly the bound (the clamp assigns it verbatim, so the comparison is
/// exact and needs no epsilon). A constraint is binding when, in the final poll round, it
/// refused a neighbouring design that <em>would have improved the objective</em> — which is
/// the definition of "this is what stopped the search", read off the search's own last act
/// rather than inferred from a tolerance on a margin.</para>
/// </summary>
/// <param name="Reason">Why the loop ended.</param>
/// <param name="BindingConstraints">Constraints that refused an improving neighbour.</param>
/// <param name="BindingBounds">Variables resting exactly on a box bound.</param>
/// <param name="Summary">One sentence a report can print.</param>
public sealed record StudyStop(
    StudyStopReason Reason,
    IReadOnlyList<string> BindingConstraints,
    IReadOnlyList<string> BindingBounds,
    string Summary);

/// <summary>Search settings. Everything here is deterministic; the study uses no randomness
/// at all, so the same study from the same start gives the same trajectory.</summary>
public sealed class StudyOptions
{
    /// <summary>Hard cap on model regenerations. Reaching it stops the study with
    /// <see cref="StudyStopReason.EvaluationBudget"/> and voids the tolerance claim.</summary>
    public int MaxEvaluations { get; init; } = 2000;

    /// <summary>A test seam, not a knob: turning off the step-RATIO adaptation is how the
    /// suite measures what it buys (a two-variable beam stops at a depth of 21.92 without
    /// it and reaches its analytic ceiling of 25 with it) and, just as importantly, that it
    /// is INERT where it claims to be — an unconstrained or single-variable study polls
    /// bit-identically either way.</summary>
    internal bool AdaptStepRatios { get; init; } = true;
}

/// <summary>
/// What a <see cref="DesignStudy"/> found: the best design, the trajectory that reached it,
/// and what stopped the search.
/// </summary>
public sealed class StudyResult
{
    internal StudyResult(
        IReadOnlyList<DesignVariable> variables,
        IReadOnlyList<StudyConstraint> constraints,
        IReadOnlyList<double> best,
        double objective,
        double violation,
        IReadOnlyList<ConstraintReading> readings,
        IReadOnlyList<StudyStep> trajectory,
        StudyStop stop,
        int evaluations,
        int regenerationFailures,
        int measurementFailures,
        bool succeeded)
    {
        Variables = variables;
        Constraints = constraints;
        Values = [.. variables.Select((v, i) => new DesignValue(v, best[i]))];
        Objective = objective;
        Violation = violation;
        ConstraintReadings = readings;
        Trajectory = trajectory;
        Stop = stop;
        Evaluations = evaluations;
        RegenerationFailures = regenerationFailures;
        MeasurementFailures = measurementFailures;
        Succeeded = succeeded;
    }

    /// <summary>The variables, in the order the caller declared them.</summary>
    public IReadOnlyList<DesignVariable> Variables { get; }

    /// <summary>The constraints, in the order the caller declared them.</summary>
    public IReadOnlyList<StudyConstraint> Constraints { get; }

    /// <summary>The best design found, one value per variable.</summary>
    public IReadOnlyList<DesignValue> Values { get; }

    /// <summary>The objective at <see cref="Values"/>.</summary>
    public double Objective { get; }

    /// <summary>Total relative constraint violation at <see cref="Values"/>; 0 when feasible.</summary>
    public double Violation { get; }

    /// <summary>Every constraint's reading at <see cref="Values"/>.</summary>
    public IReadOnlyList<ConstraintReading> ConstraintReadings { get; }

    /// <summary>Every design point visited, in order.</summary>
    public IReadOnlyList<StudyStep> Trajectory { get; }

    /// <summary>Why the search stopped and what stopped it.</summary>
    public StudyStop Stop { get; }

    /// <summary>How many times the model was regenerated.</summary>
    public int Evaluations { get; }

    /// <summary>How many designs a feature refused outright. A nonzero count is not an
    /// error — those points bound the buildable region — but it is worth reading.</summary>
    public int RegenerationFailures { get; }

    /// <summary>How many designs regenerated but could not be measured — usually a lazy
    /// <see cref="Shape"/> lowering refusing inside the first measure (see
    /// <see cref="StudyPointOutcome.MeasurementFailed"/>).</summary>
    public int MeasurementFailures { get; }

    /// <summary>Designs the model would not build or would not measure.</summary>
    public int RefusedDesigns => RegenerationFailures + MeasurementFailures;

    /// <summary>True when the study produced an answer at all (false only when the STARTING
    /// design would not rebuild).</summary>
    public bool Succeeded { get; }

    /// <summary>True when <see cref="Values"/> meets every constraint.</summary>
    public bool Feasible => Violation <= 0;

    /// <summary>
    /// How far the answer can be from the true optimum along each axis: twice that
    /// variable's <see cref="DesignVariable.StepTolerance"/>.
    ///
    /// <para>This is the optimizer's own criterion rather than a chosen number. The search
    /// halves every step together, so the last poll that failed did so at a step
    /// <c>s</c> with <c>tol &lt; s &lt;= 2·tol</c>; at that poll, moving the answer by ±s
    /// along the axis did not improve it. For an objective that is unimodal along the axis
    /// (and a feasible set that is an interval along it), the optimum is therefore within
    /// <c>s &lt;= 2·tol</c> of the answer. It holds only when
    /// <see cref="StudyStop.Reason"/> is <see cref="StudyStopReason.Converged"/> — a search
    /// stopped by its evaluation budget makes no such claim, and the property returns
    /// <see cref="double.PositiveInfinity"/> to say so.</para>
    /// </summary>
    public IReadOnlyList<double> OptimumTolerance =>
        Stop.Reason == StudyStopReason.Converged
            ? [.. Variables.Select(v => 2 * v.StepTolerance)]
            : [.. Variables.Select(_ => double.PositiveInfinity)];

    /// <summary>The value found for one variable.</summary>
    /// <exception cref="ArgumentException">The variable was not part of this study.</exception>
    public double ValueOf(DesignVariable variable)
    {
        foreach (var value in Values)
        {
            if (ReferenceEquals(value.Variable, variable))
                return value.Value;
        }
        throw new ArgumentException($"'{variable?.Name}' was not a variable of this study.", nameof(variable));
    }

    /// <summary>The reading for one constraint at the answer.</summary>
    /// <exception cref="ArgumentException">The constraint was not part of this study.</exception>
    public ConstraintReading ReadingOf(string constraintName)
    {
        foreach (var reading in ConstraintReadings)
        {
            if (reading.Name == constraintName)
                return reading;
        }
        throw new ArgumentException($"'{constraintName}' was not a constraint of this study.", nameof(constraintName));
    }

    /// <summary>
    /// The answer's values for one feature as a JSON object — the same vocabulary
    /// <see cref="FeatureHistory.SaveParameters"/> and <c>DocumentEdits.SetParameters</c>
    /// speak, so applying a study's result is one call and cannot spell a value differently
    /// from a saved file. Returns null when the study drove nothing on that feature.
    /// </summary>
    public string? ValuesFor(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var json = new JsonObject();
        foreach (var value in Values)
        {
            if (ReferenceEquals(value.Variable.Feature, feature))
            {
                json[value.Variable.Parameter] = JsonSerializer.SerializeToNode(
                    FeatureHistory.SerializeValue(value.Value), FeatureHistory.JsonOptions);
            }
        }
        return json.Count == 0 ? null : json.ToJsonString(FeatureHistory.JsonOptions);
    }

    /// <summary>
    /// The answer as undoable document edits — one <c>SetParameters</c> per driven feature,
    /// in the order the variables were declared.
    ///
    /// <para><b>A study is an analysis, not an edit</b>: it leaves the part exactly as it
    /// found it (see <see cref="DesignStudy.Minimize"/>), so adopting its answer is a
    /// deliberate, undoable act. Run these through an <see cref="UndoStack"/> —
    /// <c>using (undo.Group("Apply study")) foreach (var e in result.Edits(part)) undo.Do(e);</c>
    /// — and one Ctrl+Z takes the whole thing back.</para>
    /// </summary>
    public IReadOnlyList<DocumentEdit> Edits(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var edits = new List<DocumentEdit>();
        var seen = new List<Feature>();
        foreach (var value in Values)
        {
            if (seen.Any(f => ReferenceEquals(f, value.Variable.Feature)))
                continue;
            seen.Add(value.Variable.Feature);
            if (ValuesFor(value.Variable.Feature) is { } json)
                edits.Add(DocumentEdits.SetParameters(part, value.Variable.Feature, json));
        }
        return edits;
    }

    /// <summary>
    /// The study as a readable block: the design, its objective, every constraint's reading,
    /// and what stopped the search. A study that says "0.7 mm" without saying what stopped
    /// it is not engineering output, so this prints the binding constraint beside the answer.
    /// </summary>
    public string Report()
    {
        int column = Math.Max(
            11,
            Values.Select(v => v.Variable.Name.Length)
                .Concat(ConstraintReadings.Select(r => r.Name.Length))
                .DefaultIfEmpty(0)
                .Max());

        var text = new StringBuilder();
        text.AppendLine(Succeeded
            ? (Feasible ? "Design study: feasible optimum" : "Design study: NO FEASIBLE DESIGN")
            : "Design study: could not start");
        text.AppendLine($"  {"objective".PadRight(column)}  {Fmt(Objective)}");
        foreach (var value in Values)
        {
            text.AppendLine(
                $"  {value.Variable.Name.PadRight(column)}  {Fmt(value.Value)}"
                + $"   in [{Fmt(value.Variable.Min)}, {Fmt(value.Variable.Max)}]");
        }
        foreach (var reading in ConstraintReadings)
        {
            string relation = reading.Sense == StudySense.AtMost ? "<=" : ">=";
            text.AppendLine(
                $"  {reading.Name.PadRight(column)}  {Fmt(reading.Value)} {relation} {Fmt(reading.Limit)}   "
                + $"margin {reading.Margin * 100:F2}%{(reading.Satisfied ? "" : "   VIOLATED")}");
        }
        text.AppendLine($"  {"stop".PadRight(column)}  {Stop.Reason}: {Stop.Summary}");
        text.AppendLine(
            $"  {"evaluations".PadRight(column)}  {Evaluations}"
            + (RefusedDesigns > 0 ? $" ({RefusedDesigns} refused by the model)" : ""));
        return text.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Report();

    private static string Fmt(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>
/// Drives a part's <c>[Param]</c> values by an optimizer against a measured objective:
/// minimize mass subject to a deflection limit, maximize stiffness within a mass budget,
/// find the largest fillet a body will accept. Everything under the loop already exists —
/// <see cref="FeatureHistory"/> regenerates with prefix caching, <c>[Param(Min =, Max =)]</c>
/// declares the box, and any measurement a caller can write over a <see cref="Part"/> is an
/// objective. <b>The study is the loop.</b>
///
/// <para><b>Derivative-free, and specifically a Hooke–Jeeves pattern search.</b> A
/// regeneration is not differentiable: a parameter change can alter TOPOLOGY (a hole breaks
/// through, a fillet stops fitting), so a finite-difference gradient across such a step is
/// meaningless rather than merely noisy. Of the derivative-free family, a pattern search is
/// chosen over Nelder–Mead for three reasons that are all about this problem rather than
/// about convergence rates: <b>(a)</b> the box is the point — a simplex has no natural way
/// to rest ON a bound, and clamping collapses it, whereas a compass poll clamps to the bound
/// and keeps polling; <b>(b)</b> a refused design is simply a poll that does not improve,
/// where a simplex vertex that cannot be evaluated has to be given a fictitious value and
/// can degenerate the simplex; and <b>(c)</b> the step size IS a distance in parameter
/// space, so the stopping criterion states a bound on the answer
/// (<see cref="StudyResult.OptimumTolerance"/>) where a simplex diameter does not.</para>
///
/// <para><b>Constraints are a feasibility filter, not a penalty.</b> A penalty needs a
/// weight, and a weight silently trades one gram against one micrometre — a number nobody
/// can justify — and worse, it lets the study RETURN an infeasible design that merely scored
/// well. The filter compares points lexicographically as (violation, objective), so a
/// feasible point always beats an infeasible one and the answer meets its limits or the
/// study says it could not. While no feasible point is known the search descends on
/// violation alone, which is what gets it into the feasible region from an infeasible start.</para>
///
/// <para><b>A refused regeneration is data, not an exception.</b> It bounds the feasible
/// region, so it is recorded in the trajectory with its message and the search continues
/// from the incumbent — and because <see cref="Part.Regenerate"/> keeps the previous body
/// but NOT the previous parameter, the study writes the incumbent's values back and rebuilds
/// before moving on, exactly as <see cref="DocumentEdits"/> does for a refused edit.</para>
///
/// <para><b>Deterministic.</b> There is no randomness anywhere: the poll visits the
/// variables in declaration order, plus before minus, and takes the best improving
/// direction of a COMPLETE poll. Two runs of one study produce identical trajectories.</para>
///
/// <para><b>The study does not edit the document.</b> It restores the part to the values it
/// started from and returns the answer as data; <see cref="StudyResult.Edits"/> turns that
/// into undoable <see cref="DocumentEdit"/>s. A search evaluates hundreds of designs, and
/// none of them is history.</para>
/// </summary>
public static class DesignStudy
{
    /// <summary>
    /// Minimizes <paramref name="objective"/> over <paramref name="variables"/>, subject to
    /// <paramref name="constraints"/>. (There is deliberately no <c>Maximize</c>: negate the
    /// objective, and a report then cannot disagree with itself about which way is better.)
    /// </summary>
    /// <param name="part">The part to drive; it must carry a <see cref="FeatureHistory"/>.</param>
    /// <param name="variables">The <c>[Param]</c> values the search may move.</param>
    /// <param name="objective">Measured on the regenerated part. It is evaluated FIRST at
    /// every point, before the constraints, so a caller whose objective and constraints share
    /// one expensive analysis can run it here and let the constraints read the result.</param>
    /// <param name="constraints">Limits the answer must meet; null or empty is an
    /// unconstrained minimization, which then rests on a box bound.</param>
    /// <param name="options">Search settings.</param>
    /// <param name="progress">Optional cancellation. The reported fraction is the share of
    /// the step-halving schedule completed — the only honest fraction available, since how
    /// many poll rounds a given step size takes is not known in advance.</param>
    public static StudyResult Minimize(
        Part part,
        IReadOnlyList<DesignVariable> variables,
        Func<Part, double> objective,
        IReadOnlyList<StudyConstraint>? constraints = null,
        StudyOptions? options = null,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(objective);
        if (variables.Count == 0)
            throw new ArgumentException("A design study needs at least one variable.", nameof(variables));
        if (part.History is not { } history)
        {
            throw new ArgumentException(
                $"Part '{part.Name}' has no feature history to drive — it was built directly from geometry.",
                nameof(part));
        }

        for (int i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            if (!history.Features.Any(f => ReferenceEquals(f, variable.Feature)))
            {
                throw new ArgumentException(
                    $"'{variable.Name}' belongs to a feature that is not in part '{part.Name}''s history.",
                    nameof(variables));
            }
            for (int j = 0; j < i; j++)
            {
                if (ReferenceEquals(variables[j].Feature, variable.Feature)
                    && variables[j].Parameter == variable.Parameter)
                {
                    throw new ArgumentException(
                        $"'{variable.Name}' is declared twice; one parameter cannot be two variables.",
                        nameof(variables));
                }
            }
        }

        var search = new Search(part, variables, objective, constraints ?? [], options ?? new StudyOptions(), progress);
        return search.Run();
    }

    // The search itself. One instance per study; nothing static, nothing random.
    private sealed class Search(
        Part part,
        IReadOnlyList<DesignVariable> variables,
        Func<Part, double> objective,
        IReadOnlyList<StudyConstraint> constraints,
        StudyOptions options,
        ProgressCancel? progress)
    {
        private readonly List<StudyStep> _trajectory = [];
        private readonly double[] _original = [.. variables.Select(v => v.Current())];
        private int _evaluations;
        private int _regenerationFailures;
        private int _measurementFailures;

        // The last COMPLETE poll round that improved nothing: what the binding-constraint
        // report is read off. It is a measurement of the search's final act, not an
        // inference from a margin.
        private List<StudyStep> _lastFailedPoll = [];

        public StudyResult Run()
        {
            for (int i = 0; i < variables.Count; i++)
            {
                double value = _original[i];
                if (value < variables[i].Min || value > variables[i].Max)
                {
                    throw new ArgumentException(
                        $"'{variables[i].Name}' starts at {value.ToString("G6", CultureInfo.InvariantCulture)}, "
                        + $"outside its own study box [{variables[i].Min}, {variables[i].Max}]. "
                        + "A study searches from the design it is given.",
                        nameof(variables));
                }
            }

            double[] x = [.. _original];
            var start = Evaluate(x, _original, StudyStepKind.Start, null, 0);
            if (start.Outcome != StudyPointOutcome.Evaluated)
            {
                Restore();
                return Failed(StudyStopReason.StartFailed, x,
                    $"the starting design did not evaluate: {start.Message}");
            }
            Accept(start);

            var best = start;
            double[] step = [.. variables.Select(v => v.InitialStep)];
            double[] tolerance = [.. variables.Select(v => v.StepTolerance)];
            double halvingsNeeded = variables
                .Select(v => Math.Log2(v.InitialStep / v.StepTolerance)).Max();
            int halvings = 0;
            var reason = StudyStopReason.Converged;

            while (true)
            {
                if (Converged(step, tolerance))
                    break;
                if (_evaluations >= options.MaxEvaluations)
                {
                    reason = StudyStopReason.EvaluationBudget;
                    break;
                }
                if (progress is { } cancel)
                {
                    if (cancel.CancelRequested)
                    {
                        reason = StudyStopReason.Cancelled;
                        break;
                    }
                    cancel.Report(Math.Clamp(halvings / Math.Max(halvingsNeeded, 1), 0, 1));
                }

                var (moved, round) = Poll(best, step);
                if (moved is null)
                {
                    // The report is read off THIS poll — the one at the largest steps, from
                    // the returned incumbent — not off the ratio-adaptation sub-rounds below.
                    _lastFailedPoll = round;
                    moved = AdaptRatios(best, step, round);
                }
                if (moved is null)
                {
                    for (int i = 0; i < step.Length; i++)
                        step[i] *= 0.5;
                    halvings++;
                    continue;
                }

                var previous = best;
                best = moved;
                Accept(best);

                // Pattern (acceleration) move: the incumbent just travelled from `previous`
                // to `best`, so try the same distance again. One step, not a loop — a second
                // extrapolation is what the next poll round buys anyway, and keeping it to
                // one leaves the termination argument exactly as the poll states it.
                var pattern = new double[x.Length];
                bool distinct = false;
                for (int i = 0; i < pattern.Length; i++)
                {
                    pattern[i] = variables[i].Clamp(2 * best.Values[i] - previous.Values[i]);
                    distinct |= pattern[i] != best.Values[i];
                }
                if (distinct && _evaluations < options.MaxEvaluations)
                {
                    var extrapolated = Evaluate(pattern, best.Values, StudyStepKind.Pattern, null, 0);
                    if (Better(extrapolated, best))
                    {
                        best = extrapolated;
                        Accept(best);
                    }
                }
            }

            var bestValues = best.Values;
            var stop = Stop(reason, best);
            Restore();
            return new StudyResult(
                variables, constraints, bestValues, best.Objective, best.Violation, best.Readings,
                _trajectory, stop, _evaluations, _regenerationFailures, _measurementFailures,
                succeeded: true);
        }

        private static bool Converged(double[] step, double[] tolerance)
        {
            for (int i = 0; i < step.Length; i++)
            {
                if (step[i] > tolerance[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// One COMPLETE exploratory poll. Complete rather than opportunistic because the
        /// failed round IS the binding-constraint report — a first-improvement poll would
        /// stop before measuring the directions that say what stopped the search.
        ///
        /// <para>The poll runs in two stages. First the <b>axis</b> directions, ±e_i, which
        /// are what the convergence criterion is stated in terms of. If none of them
        /// improves, and only then, the <b>pairwise diagonals</b> ±e_i ± e_j. That second
        /// stage exists for a specific and very common failure of a plain compass search: on
        /// an ACTIVE constraint that couples two variables — trading a beam's width against
        /// its depth, say — the descent direction runs ALONG the constraint boundary, so
        /// every single-axis move is either infeasible or worse and the search stops on the
        /// first boundary point it happens to reach. Adding the diagonals is 2n(n−1) extra
        /// polls (quadratic, not the exponential full ±/0 lattice), it is deterministic, and
        /// for one variable there are no pairs at all, so a single-variable study polls
        /// exactly what it always did.</para>
        ///
        /// <para>It is a richer direction set, not a dense one: a constraint boundary whose
        /// descent direction lies outside {±e_i} ∪ {±e_i ± e_j} can still stop the search,
        /// and the honest fix for that is a dense set (MADS), which needs randomness this
        /// study will not spend. The stop report names the constraint that held it either
        /// way, so the answer is never silently presented as unconstrained.</para>
        /// </summary>
        private (Evaluation? Moved, List<StudyStep> Round) Poll(Evaluation incumbent, double[] step)
        {
            Evaluation? bestOfRound = null;
            var round = new List<StudyStep>();

            for (int i = 0; i < variables.Count; i++)
            {
                foreach (int sign in (int[])[1, -1])
                {
                    if (_evaluations >= options.MaxEvaluations)
                        return (bestOfRound, round);
                    TryDirection(incumbent, step, [(i, sign)], ref bestOfRound, round);
                }
            }
            if (bestOfRound is not null)
                return (bestOfRound, round);

            for (int i = 0; i < variables.Count; i++)
            {
                for (int j = i + 1; j < variables.Count; j++)
                {
                    foreach (int first in (int[])[1, -1])
                    {
                        foreach (int second in (int[])[1, -1])
                        {
                            if (_evaluations >= options.MaxEvaluations)
                                return (bestOfRound, round);
                            TryDirection(incumbent, step, [(i, first), (j, second)], ref bestOfRound, round);
                        }
                    }
                }
            }
            return (bestOfRound, round);
        }

        /// <summary>
        /// Halves ONE variable's step at a time and re-polls, keeping the first ratio that
        /// moves the incumbent.
        ///
        /// <para><b>Why a search over step RATIOS is what a constraint boundary needs.</b>
        /// The poll directions are fixed but their LENGTHS are not, so a diagonal
        /// (+s_i, −s_j) points along whatever slope s_j/s_i happens to be — and halving every
        /// step together, as a plain pattern search does, leaves that slope unchanged
        /// forever. On an ACTIVE constraint coupling two variables the descent direction runs
        /// along the boundary at a slope the model decides, so a fixed slope is either always
        /// too steep (every diagonal leaves the feasible set) or always too shallow (every
        /// diagonal is heavier), and the search stops on the first boundary point it reaches.
        /// Measured on a two-variable beam whose depth should have gone to its ceiling of 25:
        /// with a single shared step it stopped at <b>21.92</b>; adapting the ratio reaches
        /// the analytic answer. Halving one axis at a time makes the reachable slopes
        /// s_j·2^a / s_i·2^b — a dyadic grid that refines as the search does.</para>
        ///
        /// <para>It runs ONLY when a constraint refused a poll that would have improved the
        /// objective, because that is the only situation the ratio can matter in: an interior
        /// optimum is refused by the objective itself, where every direction is already
        /// admissible and a different slope buys nothing. So an unconstrained study, and a
        /// single-variable study of any kind, poll exactly what they always did.</para>
        ///
        /// <para>The direction set is still finite, so this is not a completeness claim: a
        /// boundary whose slope never lands between two reachable ones can still stop the
        /// search, and the honest fix for that is a direction set that becomes dense
        /// (OrthoMADS), which is filed rather than guessed at.</para>
        /// </summary>
        private Evaluation? AdaptRatios(Evaluation incumbent, double[] step, List<StudyStep> round)
        {
            if (!options.AdaptStepRatios || variables.Count < 2 || !RefusedByAConstraint(round, incumbent))
                return null;

            for (int i = 0; i < variables.Count; i++)
            {
                if (_evaluations >= options.MaxEvaluations)
                    return null;
                var trial = (double[])step.Clone();
                trial[i] *= 0.5;
                var (moved, _) = Poll(incumbent, trial);
                if (moved is not null)
                {
                    // Keep the ratio that worked: it is a measurement of the local geometry,
                    // and throwing it away would make the next round stall the same way.
                    step[i] = trial[i];
                    return moved;
                }
            }
            return null;
        }

        /// <summary>True when some polled design would have been lighter and a constraint is
        /// what refused it — the epsilon-free statement that the search is standing on a
        /// constraint boundary rather than at the objective's own minimum.</summary>
        private static bool RefusedByAConstraint(List<StudyStep> round, Evaluation incumbent)
        {
            foreach (var step in round)
            {
                if (step.Outcome == StudyPointOutcome.Evaluated
                    && step.Objective < incumbent.Objective
                    && step.Violation > incumbent.Violation)
                {
                    return true;
                }
            }
            return false;
        }

        private void TryDirection(
            Evaluation incumbent, double[] step, (int Index, int Sign)[] direction,
            ref Evaluation? bestOfRound, List<StudyStep> round)
        {
            var candidate = (double[])incumbent.Values.Clone();
            var stuck = new List<string>();
            foreach (var (index, sign) in direction)
            {
                candidate[index] = variables[index].Clamp(incumbent.Values[index] + sign * step[index]);
                if (candidate[index] == incumbent.Values[index])
                {
                    stuck.Add($"{variables[index].Name} is at its {(sign > 0 ? "upper" : "lower")} bound "
                        + (sign > 0 ? variables[index].Max : variables[index].Min)
                            .ToString("G6", CultureInfo.InvariantCulture));
                }
            }

            var moved = variables[direction[0].Index];
            double signedStep = direction[0].Sign * step[direction[0].Index];
            if (stuck.Count == direction.Length)
            {
                // Every axis of this direction clamps to where it already is, so the poll
                // point IS the incumbent and there is nothing to measure. Recorded, not
                // silently skipped: that is how the box bound gets named in the report.
                round.Add(Record(new StudyStep(
                    _trajectory.Count, StudyStepKind.Poll, moved, signedStep, candidate,
                    StudyPointOutcome.AtBound, double.NaN, double.NaN, [], false,
                    string.Join("; ", stuck))));
                return;
            }

            var evaluation = Evaluate(candidate, incumbent.Values, StudyStepKind.Poll, moved, signedStep);
            round.Add(evaluation.Step);
            if (Better(evaluation, bestOfRound ?? incumbent))
                bestOfRound = evaluation;
        }

        /// <summary>
        /// The feasibility filter: (violation, objective) compared lexicographically, with
        /// strict &lt; throughout so a tie never moves the incumbent (which is both what
        /// keeps the search from ping-ponging and what makes it deterministic).
        /// </summary>
        private static bool Better(Evaluation candidate, Evaluation incumbent)
        {
            if (candidate.Outcome != StudyPointOutcome.Evaluated)
                return false;
            if (incumbent.Outcome != StudyPointOutcome.Evaluated)
                return true;
            if (candidate.Violation != incumbent.Violation)
                return candidate.Violation < incumbent.Violation;
            return candidate.Objective < incumbent.Objective;
        }

        private Evaluation Evaluate(
            double[] values, IReadOnlyList<double> fallback, StudyStepKind kind, DesignVariable? variable, double step)
        {
            _evaluations++;
            Write(values);
            var regeneration = part.Regenerate();
            if (!regeneration.Succeeded)
            {
                _regenerationFailures++;
                // Part.Regenerate keeps the previous BODY on failure but the bad parameter
                // is still set, so the study takes it back and rebuilds — otherwise the next
                // point would be measured on a half-poisoned state.
                Write(fallback);
                part.Regenerate();
                return Failure(values, kind, variable, step, StudyPointOutcome.RegenerationFailed,
                    FirstFailure(regeneration));
            }

            double objectiveValue;
            try
            {
                objectiveValue = objective(part);
            }
            catch (Exception exception)
            {
                return Reject(values, fallback, kind, variable, step,
                    $"the objective threw {exception.GetType().Name}: {exception.Message}");
            }
            if (!double.IsFinite(objectiveValue))
            {
                return Reject(values, fallback, kind, variable, step,
                    $"the objective returned {objectiveValue.ToString(CultureInfo.InvariantCulture)}");
            }

            var readings = new ConstraintReading[constraints.Count];
            double violation = 0;
            for (int i = 0; i < constraints.Count; i++)
            {
                double measured;
                try
                {
                    measured = constraints[i].Measure(part);
                }
                catch (Exception exception)
                {
                    return Reject(values, fallback, kind, variable, step,
                        $"constraint '{constraints[i].Name}' threw {exception.GetType().Name}: {exception.Message}");
                }
                if (!double.IsFinite(measured))
                {
                    return Reject(values, fallback, kind, variable, step,
                        $"constraint '{constraints[i].Name}' returned {measured.ToString(CultureInfo.InvariantCulture)}");
                }
                readings[i] = new ConstraintReading(
                    constraints[i].Name, measured, constraints[i].Limit, constraints[i].Sense,
                    constraints[i].MarginOf(measured));
                violation += constraints[i].ViolationOf(measured);
            }

            var record = Record(new StudyStep(
                _trajectory.Count, kind, variable, step, values, StudyPointOutcome.Evaluated,
                objectiveValue, violation, readings, false, null));
            return new Evaluation(values, objectiveValue, violation, readings, StudyPointOutcome.Evaluated, null, record);
        }

        private Evaluation Reject(
            double[] values, IReadOnlyList<double> fallback, StudyStepKind kind,
            DesignVariable? variable, double step, string message)
        {
            _measurementFailures++;
            Write(fallback);
            part.Regenerate();
            return Failure(values, kind, variable, step, StudyPointOutcome.MeasurementFailed, message);
        }

        private Evaluation Failure(
            double[] values, StudyStepKind kind, DesignVariable? variable, double step,
            StudyPointOutcome outcome, string message)
        {
            var record = Record(new StudyStep(
                _trajectory.Count, kind, variable, step, values, outcome,
                double.NaN, double.NaN, [], false, message));
            return new Evaluation(values, double.NaN, double.NaN, [], outcome, message, record);
        }

        // Every visited point is recorded, so a step's Index IS its position — which is what
        // lets Accept rewrite it in place with no search, and what makes two runs' trajectories
        // comparable element by element.
        private StudyStep Record(StudyStep step)
        {
            _trajectory.Add(step);
            return step;
        }

        private void Accept(Evaluation evaluation) =>
            _trajectory[evaluation.Step.Index] = evaluation.Step with { Accepted = true };

        /// <summary>Writes the design vector through the SAME JSON seam as
        /// <see cref="FeatureHistory.SaveParameters"/> and <c>DocumentEdits.SetParameter</c>,
        /// so a study cannot spell a value differently from a saved file or a properties
        /// panel. One regeneration per point however many features are driven, which is why
        /// this does not compose <see cref="DocumentEdits"/> (one edit per feature would
        /// rebuild once per feature, and a search is not an undo history).</summary>
        private void Write(IReadOnlyList<double> values)
        {
            for (int i = 0; i < variables.Count;)
            {
                var feature = variables[i].Feature;
                var json = new JsonObject();
                for (int j = i; j < variables.Count; j++)
                {
                    if (ReferenceEquals(variables[j].Feature, feature))
                    {
                        json[variables[j].Parameter] = JsonSerializer.SerializeToNode(
                            FeatureHistory.SerializeValue(values[j]), FeatureHistory.JsonOptions);
                    }
                }
                var warnings = new List<string>();
                using (var document = JsonDocument.Parse(json.ToJsonString()))
                    FeatureHistory.ApplyParameters(feature, document.RootElement, feature.Name, warnings);
                if (warnings.Count > 0)
                    throw new InvalidOperationException($"Design study could not set parameters: {string.Join("; ", warnings)}");

                i++;
                while (i < variables.Count && ReferenceEquals(variables[i].Feature, feature))
                    i++;
            }
        }

        private void Restore()
        {
            Write(_original);
            part.Regenerate();
        }

        private StudyStop Stop(StudyStopReason reason, Evaluation best)
        {
            var bounds = new List<string>();
            for (int i = 0; i < variables.Count; i++)
            {
                // Exact comparison on purpose: the clamp ASSIGNS the bound verbatim, so a
                // variable resting on one holds its value bit for bit. A tolerance here
                // would report a nearly-bounded variable as bounded.
                if (best.Values[i] == variables[i].Min)
                    bounds.Add($"{variables[i].Name} at its lower bound {Fmt(variables[i].Min)}");
                else if (best.Values[i] == variables[i].Max)
                    bounds.Add($"{variables[i].Name} at its upper bound {Fmt(variables[i].Max)}");
            }

            // A constraint is binding when it refused a neighbour that WOULD have improved
            // the objective. That is read off the final failed poll rather than inferred
            // from a margin, so there is no epsilon anywhere in it.
            var binding = new List<string>();
            foreach (var constraint in constraints)
            {
                foreach (var step in _lastFailedPoll)
                {
                    if (step.Outcome != StudyPointOutcome.Evaluated || step.Objective >= best.Objective)
                        continue;
                    foreach (var reading in step.Constraints)
                    {
                        if (reading.Name == constraint.Name && !reading.Satisfied && !binding.Contains(constraint.Name))
                            binding.Add(constraint.Name);
                    }
                }
            }

            if (best.Violation > 0)
            {
                var missed = best.Readings.Where(r => !r.Satisfied)
                    .Select(r => $"{r.Name} reaches {Fmt(r.Value)} against a limit of {Fmt(r.Limit)} "
                        + $"({-r.Margin * 100:F1}% past it)");
                return new StudyStop(
                    StudyStopReason.NoFeasibleDesign,
                    [.. best.Readings.Where(r => !r.Satisfied).Select(r => r.Name)],
                    bounds,
                    "no design in the box meets every constraint — the best found still misses "
                    + string.Join("; ", missed));
            }

            string summary = reason switch
            {
                StudyStopReason.EvaluationBudget =>
                    $"the evaluation budget of {options.MaxEvaluations} ran out before the steps "
                    + "reached their tolerance, so the answer carries no accuracy claim",
                StudyStopReason.Cancelled => "the caller cancelled the study",
                _ when binding.Count > 0 =>
                    $"held by {string.Join(" and ", binding)}"
                    + (bounds.Count > 0 ? $", with {string.Join(" and ", bounds)}" : ""),
                _ when bounds.Count > 0 => $"held by {string.Join(" and ", bounds)}",
                _ => "an interior optimum: no constraint and no bound refused an improving neighbour",
            };
            return new StudyStop(reason, binding, bounds, summary);
        }

        private StudyResult Failed(StudyStopReason reason, double[] values, string summary) =>
            new(variables, constraints, values, double.NaN, double.NaN, [], _trajectory,
                new StudyStop(reason, [], [], summary), _evaluations, _regenerationFailures,
                _measurementFailures, succeeded: false);

        private static string FirstFailure(RegenerationResult result)
        {
            foreach (var status in result.Statuses)
            {
                if (status.Outcome == FeatureOutcome.Failed)
                    return $"{status.Name}: {status.Error}";
            }
            return "the history produced no body";
        }

        private static string Fmt(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

        private sealed record Evaluation(
            double[] Values,
            double Objective,
            double Violation,
            IReadOnlyList<ConstraintReading> Readings,
            StudyPointOutcome Outcome,
            string? Message,
            StudyStep Step);
    }
}
