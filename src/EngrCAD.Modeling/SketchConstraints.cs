using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Knobs for <see cref="ConstrainedSketch.Solve"/>.</summary>
public sealed class SketchSolverSettings
{
    /// <summary>Convergence tolerance, in sketch units. Every residual is a LENGTH
    /// (angular constraints are scaled by the sketch's characteristic length), so the
    /// default is the 1e-9 absolute weld tier: solved geometry feeds the same
    /// downstream welds as drawn geometry.</summary>
    public double Tolerance { get; init; } = EngrCAD.Core.Tolerance.Default.Linear;

    /// <summary>Maximum Levenberg–Marquardt iterations before the solve is declared
    /// failed. Sketch systems converge quadratically near a solution; 100 is generous
    /// and exists so a hopeless system fails in milliseconds, not forever.</summary>
    public int MaxIterations { get; init; } = 100;

    /// <summary>When true, a solve that converges but leaves free degrees of freedom is
    /// still reported as a failure (<see cref="ConstrainedSketch.Solve"/> throws). Off
    /// by default: an under-constrained sketch is NORMAL — dimension what matters and
    /// the rest keeps its drawn proportions.</summary>
    public bool RequireFullyConstrained { get; init; }
}

/// <summary>What a sketch constraint solve did, and what it could not do.</summary>
/// <param name="Converged">Every residual fell below the tolerance.</param>
/// <param name="Iterations">Levenberg–Marquardt steps taken.</param>
/// <param name="Residual">The largest remaining residual, in sketch units.</param>
/// <param name="FreeDegreesOfFreedom">The sketch's total geometric freedom: 2 per
/// joint, 3 per arc (center + radius; a bézier's interior control points are not
/// variables — they follow their chord). Includes the rigid-body freedoms, so a sketch
/// with nothing <see cref="ConstrainedSketch.Fix(SketchPointRef)">fixed</see> always
/// keeps at least 3.</param>
/// <param name="ConstrainedDegreesOfFreedom">The rank of the constraint Jacobian at the
/// solution — how many of those degrees of freedom the constraints actually pin.</param>
/// <param name="RedundantConstraintRows">Rows minus rank. When the solve CONVERGED this
/// counts constraints that are redundant but consistent (over-dimensioned); when it did
/// not, the same surplus is where the contradiction lives.</param>
/// <param name="Diagnostics">Human-readable notes: which constraints are worst, what is
/// still free, whether the start was a stationary configuration.</param>
public sealed record SketchSolveResult(
    bool Converged,
    int Iterations,
    double Residual,
    int FreeDegreesOfFreedom,
    int ConstrainedDegreesOfFreedom,
    int RedundantConstraintRows,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The solved sketch — null when the solve failed (the drawn sketch is
    /// never modified; a failed solve produces NOTHING).</summary>
    public Sketch? Sketch { get; init; }

    /// <summary>Degrees of freedom the constraints leave open (0 = fully constrained).</summary>
    public int RemainingDegreesOfFreedom => FreeDegreesOfFreedom - ConstrainedDegreesOfFreedom;

    /// <summary>True when the constraints do not pin the sketch completely. Not an
    /// error by itself — see <see cref="SketchSolverSettings.RequireFullyConstrained"/>.</summary>
    public bool IsUnderConstrained => RemainingDegreesOfFreedom > 0;

    /// <summary>Converged with zero remaining degrees of freedom: every joint, center
    /// and radius is pinned.</summary>
    public bool IsFullyConstrained => Converged && RemainingDegreesOfFreedom == 0;

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Converged ? "solved" : "FAILED")
            .Append($" in {Iterations} iteration{(Iterations == 1 ? "" : "s")}; ")
            .Append($"worst residual {Residual:g3}; ")
            .Append($"{ConstrainedDegreesOfFreedom} of {FreeDegreesOfFreedom} DOF constrained");
        if (RemainingDegreesOfFreedom > 0)
            text.Append($" ({RemainingDegreesOfFreedom} free)");
        if (RedundantConstraintRows > 0)
            text.Append($"; {RedundantConstraintRows} redundant row{(RedundantConstraintRows == 1 ? "" : "s")}");
        foreach (string note in Diagnostics)
            text.Append("\n  · ").Append(note);
        return text.ToString();
    }
}

/// <summary>A sketch constraint solve that could not be satisfied; carries the full
/// <see cref="SketchSolveResult"/>.</summary>
public sealed class SketchSolveException(SketchSolveResult result)
    : InvalidOperationException($"The sketch constraints could not be solved.\n{result}")
{
    /// <summary>The failed solve's report.</summary>
    public SketchSolveResult Result { get; }  = result;
}

/// <summary>A point entity of a <see cref="ConstrainedSketch"/>: a joint between
/// consecutive segments, or an arc's center. Create via
/// <see cref="ConstrainedSketch.Point"/> / <see cref="ConstrainedSketch.CenterOf"/>.</summary>
public readonly struct SketchPointRef
{
    internal ConstrainedSketch Owner { get; }
    internal int Variable { get; }
    internal string Description { get; }

    internal SketchPointRef(ConstrainedSketch owner, int variable, string description)
    {
        Owner = owner;
        Variable = variable;
        Description = description;
    }

    public override string ToString() => Description;
}

/// <summary>A straight-line segment entity of a <see cref="ConstrainedSketch"/>. Its
/// direction runs start→end in the sketch's normalized (counter-clockwise outer loop)
/// segment order.</summary>
public readonly struct SketchLineRef
{
    internal ConstrainedSketch Owner { get; }
    internal int P1 { get; }
    internal int P2 { get; }
    internal string Description { get; }

    internal SketchLineRef(ConstrainedSketch owner, int p1, int p2, string description)
    {
        Owner = owner;
        P1 = p1;
        P2 = p2;
        Description = description;
    }

    public override string ToString() => Description;
}

/// <summary>An arc or full-circle segment entity of a <see cref="ConstrainedSketch"/>,
/// carrying its center and radius variables.</summary>
public readonly struct SketchArcRef
{
    internal ConstrainedSketch Owner { get; }
    internal int Center { get; }
    internal int Radius { get; }

    /// <summary>Variable indices of the arc's endpoint joints (−1 for a full-circle
    /// loop) — what lets Tangent recognize an ADJACENT line/arc and use the exact
    /// first-order tangency-at-the-joint formulation.</summary>
    internal int StartJoint { get; }
    internal int EndJoint { get; }

    internal string Description { get; }

    internal SketchArcRef(
        ConstrainedSketch owner, int center, int radius, int startJoint, int endJoint, string description)
    {
        Owner = owner;
        Center = center;
        Radius = radius;
        StartJoint = startJoint;
        EndJoint = endJoint;
        Description = description;
    }

    public override string ToString() => Description;
}

/// <summary>
/// The variational 2D constraint layer over a drawn <see cref="Sketch"/> — Onshape's
/// constraint vocabulary (CadQuery's <c>Sketch.constrain(...).solve()</c> is the API
/// reference) solved by the MateSolver doctrine.
///
/// <code>
/// var cs = drawn.Constrain();
/// cs.Horizontal(cs.Line(0))
///   .Perpendicular(cs.Line(0), cs.Line(1))
///   .Distance(cs.Point(0), cs.Point(1), 30)
///   .Fix(cs.Point(0));
/// Sketch solved = cs.Solve().Sketch!;      // an ordinary Sketch — extrude it as usual
/// </code>
///
/// <para><b>Entities and variables.</b> Segments are addressed in the sketch's
/// normalized order (the order <see cref="Sketch.ToCurves"/> reports — outer loops are
/// counter-clockwise). Endpoints shared between consecutive segments are ONE point
/// variable (<see cref="Point"/>); arcs carry center + radius variables tied to their
/// endpoint joints by internal consistency rows; a loop that is a single full circle
/// has center + radius only (constrain it via <see cref="CenterOf"/> and
/// <see cref="Radius"/>). Bézier segments expose only their endpoint joints in v1 —
/// their control points follow the solved chord.</para>
///
/// <para><b>The drawn configuration is the seed AND the branch selector.</b> The solve
/// starts from the sketch as drawn, and every constraint with a discrete choice reads
/// it off the drawing: a tangent arc stays on the side of the line it was drawn on,
/// arc–arc tangency picks external/internal by whichever the drawing is closer to, and
/// an arc keeps its drawn sweep direction and large/small branch. Draw roughly the
/// shape you mean, then let the dimensions make it exact.</para>
///
/// <para><b>Under-constrained is NORMAL, and the solve changes as little as possible.</b>
/// A Levenberg–Marquardt step lies in the row space of the Jacobian, so motions no
/// constraint can see are never touched and the damping pulls every step toward the
/// seed: unconstrained geometry keeps its drawn proportions. The report always states
/// the remaining degrees of freedom (rank-revealing, so redundant rows are not
/// miscounted); over-constrained-but-consistent sketches converge and report the
/// redundant row count.</para>
///
/// <para><b>It refuses loudly.</b> A solve that cannot satisfy the constraints produces
/// NOTHING (the drawn sketch is immutable and <see cref="SketchSolveResult.Sketch"/>
/// stays null) and names the constraints carrying the residual; a start with no
/// first-order step (two lines drawn exactly parallel under a Perpendicular constraint)
/// is named as a stationary configuration rather than nudged at random.</para>
/// </summary>
public sealed class ConstrainedSketch
{
    private readonly SketchVariables _map;
    private readonly List<SketchConstraint> _constraints = [];
    private readonly List<string> _constraintNames = [];

    internal ConstrainedSketch(Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        Source = sketch;
        _map = SketchVariables.Build(sketch);
    }

    /// <summary>Begins constraining <paramref name="sketch"/> (equivalent to
    /// <see cref="Sketch.Constrain"/>).</summary>
    public static ConstrainedSketch From(Sketch sketch) => new(sketch);

    /// <summary>The drawn sketch — the solver's seed. Never modified.</summary>
    public Sketch Source { get; }

    /// <summary>The constraints added so far, by display name.</summary>
    public IReadOnlyList<string> Constraints => _constraintNames;

    // ================================================================= entities

    /// <summary>Joint <paramref name="joint"/> of the outer loop — the shared endpoint
    /// where segment <c>joint</c> starts (and segment <c>joint − 1</c> ends).</summary>
    public SketchPointRef Point(int joint) => JointRef(0, joint);

    /// <summary>Joint <paramref name="joint"/> of hole <paramref name="hole"/>.</summary>
    public SketchPointRef HolePoint(int hole, int joint) => JointRef(HoleLoop(hole), joint);

    /// <summary>Straight segment <paramref name="segment"/> of the outer loop.</summary>
    public SketchLineRef Line(int segment) => LineRef(0, segment);

    /// <summary>Straight segment <paramref name="segment"/> of hole <paramref name="hole"/>.</summary>
    public SketchLineRef HoleLine(int hole, int segment) => LineRef(HoleLoop(hole), segment);

    /// <summary>Arc (or full-circle) segment <paramref name="segment"/> of the outer loop.</summary>
    public SketchArcRef Arc(int segment) => ArcRef(0, segment);

    /// <summary>Arc (or full-circle) segment <paramref name="segment"/> of hole
    /// <paramref name="hole"/> — a hole drawn with <c>Sketch.Circle</c> is its segment 0.</summary>
    public SketchArcRef HoleArc(int hole, int segment) => ArcRef(HoleLoop(hole), segment);

    /// <summary>An arc's center as a point entity — usable in <see cref="Coincident"/>,
    /// <see cref="Distance(SketchPointRef, SketchPointRef, double)"/>,
    /// <see cref="Fix(SketchPointRef)"/>, …</summary>
    public SketchPointRef CenterOf(SketchArcRef arc)
    {
        RequireOwned(arc.Owner, arc.Description);
        return new SketchPointRef(this, arc.Center, $"{arc.Description} center");
    }

    // ================================================================ constraints

    /// <summary>Two points coincide.</summary>
    public ConstrainedSketch Coincident(SketchPointRef a, SketchPointRef b) =>
        Add(new CoincidentConstraint(Owned(a).Variable, Owned(b).Variable)
            { Name = $"Coincident({a}, {b})" });

    /// <summary>The line is horizontal (its endpoints share a y coordinate).</summary>
    public ConstrainedSketch Horizontal(SketchLineRef line)
    {
        RequireOwned(line.Owner, line.Description);
        return Add(new AlignedConstraint(line.P1, line.P2, horizontal: true)
            { Name = $"Horizontal({line})" });
    }

    /// <summary>Two points share a y coordinate.</summary>
    public ConstrainedSketch Horizontal(SketchPointRef a, SketchPointRef b) =>
        Add(new AlignedConstraint(Owned(a).Variable, Owned(b).Variable, horizontal: true)
            { Name = $"Horizontal({a}, {b})" });

    /// <summary>The line is vertical (its endpoints share an x coordinate).</summary>
    public ConstrainedSketch Vertical(SketchLineRef line)
    {
        RequireOwned(line.Owner, line.Description);
        return Add(new AlignedConstraint(line.P1, line.P2, horizontal: false)
            { Name = $"Vertical({line})" });
    }

    /// <summary>Two points share an x coordinate.</summary>
    public ConstrainedSketch Vertical(SketchPointRef a, SketchPointRef b) =>
        Add(new AlignedConstraint(Owned(a).Variable, Owned(b).Variable, horizontal: false)
            { Name = $"Vertical({a}, {b})" });

    /// <summary>Two lines are parallel.</summary>
    public ConstrainedSketch Parallel(SketchLineRef a, SketchLineRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Add(new DirectionConstraint(a.P1, a.P2, b.P1, b.P2, parallel: true, 0)
            { Name = $"Parallel({a}, {b})" });
    }

    /// <summary>Two lines are perpendicular.</summary>
    public ConstrainedSketch Perpendicular(SketchLineRef a, SketchLineRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Add(new DirectionConstraint(a.P1, a.P2, b.P1, b.P2, parallel: false, 0)
            { Name = $"Perpendicular({a}, {b})" });
    }

    /// <summary>The angle between the two lines' directions (start→end, in the
    /// normalized segment order) is <paramref name="radians"/>, measured unsigned in
    /// [0, π]. An angle of 0 or π is refused — use <see cref="Parallel"/>, whose cross
    /// form stays first-order where the dot form's gradient vanishes.</summary>
    public ConstrainedSketch Angle(SketchLineRef a, SketchLineRef b, double radians)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        // Angles are dimensionless, so this guard is legitimately absolute (the sweep
        // guard convention in SketchBuilder): at sin θ = 0 the dot residual has no
        // first-order behaviour at its own solution.
        if (Math.Abs(Math.Sin(radians)) < 1e-9)
            throw new ArgumentOutOfRangeException(nameof(radians),
                "An angle of 0 or π has no first-order dot residual at its own solution; " +
                "use Parallel instead.");
        return Add(new DirectionConstraint(a.P1, a.P2, b.P1, b.P2, parallel: false, Math.Cos(radians))
            { Name = $"Angle({a}, {b}) = {radians:g6} rad" });
    }

    /// <summary>
    /// The line is tangent to the arc. When they are ADJACENT (they share an endpoint
    /// joint — a line running into its fillet) this is exact G1 at the joint: the
    /// radius direction is constrained perpendicular to the line, which together with
    /// the arc's built-in endpoint consistency makes the shared joint the tangency
    /// point, first-order in every motion. Otherwise (a free-standing arc, e.g. a hole
    /// circle) the arc's center is held one radius from the line's carrier, on the side
    /// it was DRAWN on (the drawn configuration is the branch selector; a center drawn
    /// exactly on the line takes the left side of the line's direction).
    /// </summary>
    public ConstrainedSketch Tangent(SketchLineRef line, SketchArcRef arc)
    {
        RequireOwned(line.Owner, line.Description);
        RequireOwned(arc.Owner, arc.Description);
        string name = $"Tangent({line}, {arc})";

        int joint = SharedJoint(arc, line.P1, line.P2);
        if (joint >= 0)
            return Add(new TangentAtJointConstraint(line.P1, line.P2, arc.Center, joint)
                { Name = name });

        var seed = _map.Seed;
        var p1 = SketchVariables.Point(seed, line.P1);
        var direction = SketchVariables.Point(seed, line.P2) - p1;
        double cross = direction.Cross(SketchVariables.Point(seed, arc.Center) - p1);
        double side = cross >= 0 ? 1 : -1;
        return Add(new TangentLineArcConstraint(line.P1, line.P2, arc.Center, arc.Radius, side)
            { Name = name });
    }

    /// <summary>
    /// The two arcs are tangent. ADJACENT arcs (sharing an endpoint joint) get exact
    /// G1 at the joint — their radius directions at the shared joint are constrained
    /// collinear, first-order in every motion (external vs internal follows from the
    /// drawn side, which LM preserves). Free-standing arcs (hole circles) get the
    /// center-distance form, |cA − cB| = rA + rB or ±(rA − rB), the branch selected by
    /// whichever the DRAWN configuration is closer to (ties take external).
    /// </summary>
    public ConstrainedSketch Tangent(SketchArcRef a, SketchArcRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        string name = $"Tangent({a}, {b})";

        int joint = SharedJoint(a, b.StartJoint, b.EndJoint);
        if (joint >= 0)
            return Add(new DirectionConstraint(joint, a.Center, joint, b.Center, parallel: true, 0)
                { Name = name });

        var seed = _map.Seed;
        double separation =
            (SketchVariables.Point(seed, a.Center) - SketchVariables.Point(seed, b.Center)).Length;
        double radiusA = seed[a.Radius], radiusB = seed[b.Radius];
        double innerSign = radiusA >= radiusB ? 1 : -1;
        bool external = Math.Abs(separation - (radiusA + radiusB))
            <= Math.Abs(separation - innerSign * (radiusA - radiusB));
        return Add(new TangentArcArcConstraint(a.Center, a.Radius, b.Center, b.Radius, external, innerSign)
            { Name = name });
    }

    /// <summary>The arc's endpoint joint that coincides (as a VARIABLE — same index)
    /// with one of the two given point variables, or −1. Adjacency is structural, never
    /// a coordinate comparison.</summary>
    private static int SharedJoint(in SketchArcRef arc, int pointA, int pointB)
    {
        if (arc.StartJoint >= 0 && (arc.StartJoint == pointA || arc.StartJoint == pointB))
            return arc.StartJoint;
        if (arc.EndJoint >= 0 && (arc.EndJoint == pointA || arc.EndJoint == pointB))
            return arc.EndJoint;
        return -1;
    }

    /// <summary>Two lines have equal length.</summary>
    public ConstrainedSketch EqualLength(SketchLineRef a, SketchLineRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Add(new EqualLengthConstraint(a.P1, a.P2, b.P1, b.P2)
            { Name = $"EqualLength({a}, {b})" });
    }

    /// <summary>Two arcs have equal radii.</summary>
    public ConstrainedSketch EqualRadius(SketchArcRef a, SketchArcRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Add(new EqualScalarConstraint(a.Radius, b.Radius)
            { Name = $"EqualRadius({a}, {b})" });
    }

    /// <summary>Two arcs share a center.</summary>
    public ConstrainedSketch Concentric(SketchArcRef a, SketchArcRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Add(new CoincidentConstraint(a.Center, b.Center)
            { Name = $"Concentric({a}, {b})" });
    }

    /// <summary>Pins a point exactly where it was drawn — the sketch's datum. Without a
    /// fix (or dimensions to one), a sketch always keeps its rigid-body freedoms and
    /// can never reach zero remaining DOF.</summary>
    public ConstrainedSketch Fix(SketchPointRef point)
    {
        var owned = Owned(point);
        var seed = _map.Seed;
        return Add(new FixConstraint(owned.Variable, seed[owned.Variable], seed[owned.Variable + 1])
            { Name = $"Fix({point})" });
    }

    /// <summary>Pins both endpoints of a line where they were drawn.</summary>
    public ConstrainedSketch Fix(SketchLineRef line)
    {
        RequireOwned(line.Owner, line.Description);
        var seed = _map.Seed;
        Add(new FixConstraint(line.P1, seed[line.P1], seed[line.P1 + 1])
            { Name = $"Fix({line} start)" });
        return Add(new FixConstraint(line.P2, seed[line.P2], seed[line.P2 + 1])
            { Name = $"Fix({line} end)" });
    }

    /// <summary>Pins an arc's center and radius as drawn (its endpoints may still slide
    /// along it unless they are pinned too).</summary>
    public ConstrainedSketch Fix(SketchArcRef arc)
    {
        RequireOwned(arc.Owner, arc.Description);
        var seed = _map.Seed;
        Add(new FixConstraint(arc.Center, seed[arc.Center], seed[arc.Center + 1])
            { Name = $"Fix({arc} center)" });
        return Add(new ScalarValueConstraint(arc.Radius, seed[arc.Radius])
            { Name = $"Fix({arc} radius)" });
    }

    /// <summary>Dimension: the distance between two points is
    /// <paramref name="distance"/> (&gt; 0; for zero use <see cref="Coincident"/>,
    /// which stays differentiable at its own solution).</summary>
    public ConstrainedSketch Distance(SketchPointRef a, SketchPointRef b, double distance)
    {
        if (!(distance > 0))
            throw new ArgumentOutOfRangeException(nameof(distance),
                "Point-to-point distance must be positive; a zero distance is Coincident.");
        return Add(new DistancePointsConstraint(Owned(a).Variable, Owned(b).Variable, distance)
            { Name = $"Distance({a}, {b}) = {distance:g6}" });
    }

    /// <summary>Dimension: the point sits <paramref name="distance"/> (≥ 0) from the
    /// line's carrier, on the side it was DRAWN on (0 puts the point exactly on the
    /// line — the residual is signed, so this stays smooth).</summary>
    public ConstrainedSketch Distance(SketchPointRef point, SketchLineRef line, double distance)
    {
        var owned = Owned(point);
        RequireOwned(line.Owner, line.Description);
        if (distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance),
                "Point-to-line distance is measured on the drawn side; it cannot be negative.");
        var seed = _map.Seed;
        var p1 = SketchVariables.Point(seed, line.P1);
        var direction = SketchVariables.Point(seed, line.P2) - p1;
        double cross = direction.Cross(SketchVariables.Point(seed, owned.Variable) - p1);
        double side = cross >= 0 ? 1 : -1;
        return Add(new DistancePointLineConstraint(owned.Variable, line.P1, line.P2, distance, side)
            { Name = $"Distance({point}, {line}) = {distance:g6}" });
    }

    /// <summary>Dimension: the arc's radius is <paramref name="radius"/> (&gt; 0).</summary>
    public ConstrainedSketch Radius(SketchArcRef arc, double radius)
    {
        RequireOwned(arc.Owner, arc.Description);
        if (!(radius > 0))
            throw new ArgumentOutOfRangeException(nameof(radius));
        return Add(new ScalarValueConstraint(arc.Radius, radius)
            { Name = $"Radius({arc}) = {radius:g6}" });
    }

    /// <summary>Dimension: the arc's diameter is <paramref name="diameter"/> (&gt; 0).</summary>
    public ConstrainedSketch Diameter(SketchArcRef arc, double diameter)
    {
        RequireOwned(arc.Owner, arc.Description);
        if (!(diameter > 0))
            throw new ArgumentOutOfRangeException(nameof(diameter));
        return Add(new ScalarValueConstraint(arc.Radius, diameter / 2)
            { Name = $"Diameter({arc}) = {diameter:g6}" });
    }

    // ================================================================== solving

    /// <summary>
    /// Solves the constraints and returns the report with the solved
    /// <see cref="SketchSolveResult.Sketch"/>. Throws <see cref="SketchSolveException"/>
    /// when the constraints cannot all be satisfied (the drawn sketch is untouched and
    /// no solved sketch exists), or when
    /// <see cref="SketchSolverSettings.RequireFullyConstrained"/> is set and degrees of
    /// freedom remain. Re-solving after adding more constraints always starts from the
    /// DRAWN sketch again — the seed is the drawing, not a previous solution.
    /// </summary>
    public SketchSolveResult Solve(SketchSolverSettings? settings = null)
    {
        settings ??= new SketchSolverSettings();
        var result = TrySolve(settings);
        if (!result.Converged || (settings.RequireFullyConstrained && result.IsUnderConstrained))
            throw new SketchSolveException(result);
        return result;
    }

    /// <summary><see cref="Solve"/> without the exception: returns the report either
    /// way; <see cref="SketchSolveResult.Sketch"/> is null unless the solve converged.</summary>
    public SketchSolveResult TrySolve(SketchSolverSettings? settings = null)
    {
        settings ??= new SketchSolverSettings();
        var diagnostics = new List<string>();

        if (_constraints.Count == 0)
        {
            diagnostics.Add("no constraints — the sketch is returned as drawn");
            diagnostics.Add(FreedomNote(_map.Count));
            return new SketchSolveResult(true, 0, 0, _map.Count, 0, 0, diagnostics)
                { Sketch = Source };
        }

        var system = new List<SketchConstraint>(_constraints);
        AddInternalConstraints(system);

        var outcome = SketchLevenberg.Run(_map.Seed, system, _map.CharacteristicLength, settings);
        int redundant = outcome.Rows - outcome.Rank;

        bool converged = outcome.Converged;
        Sketch? solved = null;
        if (converged)
        {
            try
            {
                solved = _map.Rebuild(outcome.Solution);
            }
            catch (ArgumentException degenerate)
            {
                // The residuals were satisfied numerically but the geometry they
                // describe is not a valid sketch (collapsed area, broken closure).
                // Refuse loudly rather than hand back garbage.
                converged = false;
                diagnostics.Add($"the solved configuration is degenerate: {degenerate.Message}");
            }
        }
        else
        {
            WorstConstraints(system, outcome.Residuals, diagnostics);
            if (outcome.Steps == 0 && _map.Count > 0)
            {
                // Nonzero residual, zero gradient: a STATIONARY start. The textbook
                // case is Perpendicular (or Angle) between lines drawn exactly
                // parallel — d/dθ cos θ = 0 at θ = 0, so no first-order step exists.
                // Randomly nudging and retrying would "sometimes converge", which is
                // worse than saying so.
                diagnostics.Add(
                    "no first-order motion improves the residual: the drawn configuration is a " +
                    "stationary point for these constraints (a Perpendicular or Angle constraint " +
                    "between lines drawn exactly parallel is the usual cause). Redraw the sketch " +
                    "roughly in the shape you mean and solve again.");
            }
        }

        if (!converged)
            diagnostics.Add("no solved sketch was produced — the drawn sketch is unchanged");

        int remaining = _map.Count - outcome.Rank;
        if (remaining > 0)
            diagnostics.Add(FreedomNote(remaining));
        if (converged && redundant > 0)
            diagnostics.Add(
                $"{redundant} constraint row{(redundant == 1 ? " is" : "s are")} redundant but " +
                "consistent — the sketch is over-dimensioned; removing a duplicate constraint " +
                "would not change the result");

        return new SketchSolveResult(
            converged, outcome.Iterations, outcome.Residual,
            _map.Count, outcome.Rank, redundant, diagnostics)
            { Sketch = solved };
    }

    private static string FreedomNote(int remaining) =>
        $"{remaining} degree{(remaining == 1 ? "" : "s")} of freedom remain: the constraints do " +
        "not pin the sketch completely (add dimensions, or Fix a point) — unconstrained " +
        "geometry keeps its drawn proportions";

    private static void WorstConstraints(
        List<SketchConstraint> system, double[] residuals, List<string> diagnostics)
    {
        var offenders = new List<(string Name, double Peak)>(system.Count);
        int row = 0;
        foreach (var constraint in system)
        {
            double peak = 0;
            for (int i = 0; i < constraint.Rows; i++)
                peak = Math.Max(peak, Math.Abs(residuals[row + i]));
            row += constraint.Rows;
            offenders.Add((constraint.Name, peak));
        }
        foreach (var (name, peak) in offenders.OrderByDescending(o => o.Peak).Take(3))
            diagnostics.Add($"'{name}' is off by {peak:g4}");
    }

    /// <summary>The endpoint-consistency rows tying each arc's center/radius variables
    /// to its joint variables — |joint − center| = radius at both ends. These are part
    /// of the SYSTEM, not user constraints: they are what makes "an arc" mean an arc
    /// through any solve, and they count toward the rank like any other row.</summary>
    private void AddInternalConstraints(List<SketchConstraint> system)
    {
        for (int l = 0; l < _map.Loops.Count; l++)
        {
            var loop = _map.Loops[l];
            if (loop.SingleCircle)
                continue;
            for (int s = 0; s < loop.Segments.Count; s++)
            {
                if (loop.CenterVars[s] < 0)
                    continue;
                int start = loop.JointVars[s];
                int end = loop.JointVars[(s + 1) % loop.JointCount];
                system.Add(new ArcEndpointConstraint(start, loop.CenterVars[s], loop.RadiusVars[s])
                    { Name = $"{LoopName(l)} arc {s} endpoint consistency (start)" });
                system.Add(new ArcEndpointConstraint(end, loop.CenterVars[s], loop.RadiusVars[s])
                    { Name = $"{LoopName(l)} arc {s} endpoint consistency (end)" });
            }
        }
    }

    // ================================================================ plumbing

    private ConstrainedSketch Add(SketchConstraint constraint)
    {
        _constraints.Add(constraint);
        _constraintNames.Add(constraint.Name);
        return this;
    }

    private static string LoopName(int loop) => loop == 0 ? "outer" : $"hole {loop - 1}";

    private int HoleLoop(int hole)
    {
        if (hole < 0 || hole >= _map.Loops.Count - 1)
            throw new ArgumentOutOfRangeException(nameof(hole),
                $"The sketch has {_map.Loops.Count - 1} hole(s); hole {hole} does not exist.");
        return hole + 1;
    }

    private SketchPointRef JointRef(int loop, int joint)
    {
        var map = _map.Loops[loop];
        if (map.SingleCircle)
            throw new ArgumentException(
                $"The {LoopName(loop)} loop is a full circle, which has no joints; constrain its " +
                "center (CenterOf) or radius instead.");
        if (joint < 0 || joint >= map.JointCount)
            throw new ArgumentOutOfRangeException(nameof(joint),
                $"The {LoopName(loop)} loop has {map.JointCount} joints; joint {joint} does not exist.");
        return new SketchPointRef(this, map.JointVars[joint], $"{LoopName(loop)} point {joint}");
    }

    private SketchLineRef LineRef(int loop, int segment)
    {
        var map = _map.Loops[loop];
        RequireSegment(loop, segment);
        return map.Segments[segment] switch
        {
            LineSeg => new SketchLineRef(this,
                map.JointVars[segment], map.JointVars[(segment + 1) % map.JointCount],
                $"{LoopName(loop)} line {segment}"),
            ArcSeg => throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is an arc — use Arc({segment})."),
            _ => throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is a bézier; only its endpoint " +
                "joints (Point) can be constrained."),
        };
    }

    private SketchArcRef ArcRef(int loop, int segment)
    {
        var map = _map.Loops[loop];
        RequireSegment(loop, segment);
        if (map.CenterVars[segment] < 0)
            throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is not an arc " +
                $"(it is a {(map.Segments[segment] is LineSeg ? "line" : "bézier")}).");
        bool circle = map.Segments[segment] is ArcSeg { IsFullCircle: true };
        int startJoint = map.SingleCircle ? -1 : map.JointVars[segment];
        int endJoint = map.SingleCircle ? -1 : map.JointVars[(segment + 1) % map.JointCount];
        return new SketchArcRef(this, map.CenterVars[segment], map.RadiusVars[segment],
            startJoint, endJoint, $"{LoopName(loop)} {(circle ? "circle" : "arc")} {segment}");
    }

    private void RequireSegment(int loop, int segment)
    {
        int count = _map.Loops[loop].Segments.Count;
        if (segment < 0 || segment >= count)
            throw new ArgumentOutOfRangeException(nameof(segment),
                $"The {LoopName(loop)} loop has {count} segments; segment {segment} does not exist.");
    }

    private SketchPointRef Owned(in SketchPointRef point)
    {
        RequireOwned(point.Owner, point.Description);
        return point;
    }

    private void RequireOwned(ConstrainedSketch? owner, string? what)
    {
        if (!ReferenceEquals(owner, this))
            throw new ArgumentException(
                $"'{what ?? "the reference"}' belongs to a different ConstrainedSketch — create " +
                "entity references from the instance you solve.");
    }
}
