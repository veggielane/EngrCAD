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

    /// <summary>The canonical parseable term that rebuilds this ref through the public
    /// accessors — <c>point(3)</c>, <c>holePoint(0,2)</c>, <c>centerOf(arc(1))</c> —
    /// the serialized form (the GeometryRefs rule: one string, machine-read; the prose
    /// <see cref="Description"/> stays for humans and error messages).</summary>
    internal string Descriptor { get; }

    internal SketchPointRef(ConstrainedSketch owner, int variable, string description,
        string descriptor = "")
    {
        Owner = owner;
        Variable = variable;
        Description = description;
        Descriptor = descriptor;
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

    internal string Descriptor { get; }

    internal SketchLineRef(ConstrainedSketch owner, int p1, int p2, string description,
        string descriptor = "")
    {
        Owner = owner;
        P1 = p1;
        P2 = p2;
        Description = description;
        Descriptor = descriptor;
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

    internal string Descriptor { get; }

    internal SketchArcRef(
        ConstrainedSketch owner, int center, int radius, int startJoint, int endJoint, string description, string descriptor = "")
    {
        Owner = owner;
        Center = center;
        Radius = radius;
        StartJoint = startJoint;
        EndJoint = endJoint;
        Description = description;
        Descriptor = descriptor;
    }

    public override string ToString() => Description;
}

/// <summary>Which end of a <see cref="SketchCurveRef"/> a tangency is taken at.</summary>
public enum SketchCurveEnd
{
    /// <summary>The v = 0 joint, where the segment starts.</summary>
    Start,

    /// <summary>The v = 1 joint, where the segment ends.</summary>
    End,
}

/// <summary>
/// A cubic BÉZIER or ELLIPTICAL-ARC segment of a <see cref="ConstrainedSketch"/> — the two
/// segment kinds that carry no shape variables of their own and instead ride the SIMILARITY
/// of their two endpoint joints (a bézier's control points and an ellipse's centre and both
/// semi-axis vectors all move with it).
/// <para>That is what makes them constrainable at all without a second variable scheme: the
/// carrier is a fixed shape times a live chord, so a point-on-carrier or a tangency residual
/// is written once against the DRAWN geometry and evaluated through the chord.</para>
/// </summary>
public readonly struct SketchCurveRef
{
    internal ConstrainedSketch Owner { get; }

    /// <summary>Start joint variable, or −1 when the loop is one closed curve and the
    /// carrier therefore cannot move at all.</summary>
    internal int Start { get; }

    internal int End { get; }
    internal SketchSegment Segment { get; }
    internal string Description { get; }

    internal string Descriptor { get; }

    internal SketchCurveRef(
        ConstrainedSketch owner, int start, int end, SketchSegment segment, string description, string descriptor = "")
    {
        Owner = owner;
        Start = start;
        End = end;
        Segment = segment;
        Description = description;
        Descriptor = descriptor;
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

    /// <summary>Seeds of the AUXILIARY unknowns some constraints carry — today only a
    /// point-on-bézier foot parameter, which has no geometry of its own to live on.
    /// They sit after the sketch's own variables, so <c>Rebuild</c> reads exactly the
    /// prefix it always did and the DOF report counts the whole system.</summary>
    private readonly List<double> _auxiliary = [];

    private int AddAuxiliary(double seed)
    {
        _auxiliary.Add(seed);
        return _map.Count + _auxiliary.Count - 1;
    }

    private double[] SolverSeed()
    {
        if (_auxiliary.Count == 0)
            return _map.Seed;
        var seed = new double[_map.Count + _auxiliary.Count];
        _map.Seed.CopyTo(seed, 0);
        _auxiliary.CopyTo(seed, _map.Count);
        return seed;
    }

    private int TotalVariables => _map.Count + _auxiliary.Count;

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

    // Every public constraint call also records ITSELF, as the canonical tokens that
    // replay it through the same public method — the serialized form. Recorded AFTER
    // the Add succeeds, so a refusal leaves no record behind.
    private readonly List<string[]> _records = [];

    private ConstrainedSketch Recorded(ConstrainedSketch result, params string[] record)
    {
        _records.Add(record);
        return result;
    }

    private static string Num(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>Bézier or elliptical-arc segment <paramref name="segment"/> of the outer
    /// loop — the two kinds whose shape rides their endpoint joints.</summary>
    public SketchCurveRef Curve(int segment) => CurveRef(0, segment);

    /// <summary>Bézier or elliptical-arc segment <paramref name="segment"/> of hole
    /// <paramref name="hole"/>.</summary>
    public SketchCurveRef HoleCurve(int hole, int segment) => CurveRef(HoleLoop(hole), segment);

    /// <summary>An arc's center as a point entity — usable in <see cref="Coincident"/>,
    /// <see cref="Distance(SketchPointRef, SketchPointRef, double)"/>,
    /// <see cref="Fix(SketchPointRef)"/>, …</summary>
    public SketchPointRef CenterOf(SketchArcRef arc)
    {
        RequireOwned(arc.Owner, arc.Description);
        return new SketchPointRef(this, arc.Center, $"{arc.Description} center",
            $"centerOf({arc.Descriptor})");
    }

    // ================================================================ constraints

    /// <summary>Two points coincide.</summary>
    public ConstrainedSketch Coincident(SketchPointRef a, SketchPointRef b) =>
        Recorded(Add(new CoincidentConstraint(Owned(a).Variable, Owned(b).Variable)
            { Name = $"Coincident({a}, {b})" }), "Coincident", a.Descriptor, b.Descriptor);

    /// <summary>The line is horizontal (its endpoints share a y coordinate).</summary>
    public ConstrainedSketch Horizontal(SketchLineRef line)
    {
        RequireOwned(line.Owner, line.Description);
        return Recorded(Add(new AlignedConstraint(line.P1, line.P2, horizontal: true)
            { Name = $"Horizontal({line})" }), "Horizontal", line.Descriptor);
    }

    /// <summary>Two points share a y coordinate.</summary>
    public ConstrainedSketch Horizontal(SketchPointRef a, SketchPointRef b) =>
        Recorded(Add(new AlignedConstraint(Owned(a).Variable, Owned(b).Variable, horizontal: true)
            { Name = $"Horizontal({a}, {b})" }), "Horizontal", a.Descriptor, b.Descriptor);

    /// <summary>The line is vertical (its endpoints share an x coordinate).</summary>
    public ConstrainedSketch Vertical(SketchLineRef line)
    {
        RequireOwned(line.Owner, line.Description);
        return Recorded(Add(new AlignedConstraint(line.P1, line.P2, horizontal: false)
            { Name = $"Vertical({line})" }), "Vertical", line.Descriptor);
    }

    /// <summary>Two points share an x coordinate.</summary>
    public ConstrainedSketch Vertical(SketchPointRef a, SketchPointRef b) =>
        Recorded(Add(new AlignedConstraint(Owned(a).Variable, Owned(b).Variable, horizontal: false)
            { Name = $"Vertical({a}, {b})" }), "Vertical", a.Descriptor, b.Descriptor);

    /// <summary>Two lines are parallel.</summary>
    public ConstrainedSketch Parallel(SketchLineRef a, SketchLineRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Recorded(Add(new DirectionConstraint(a.P1, a.P2, b.P1, b.P2, parallel: true, 0)
            { Name = $"Parallel({a}, {b})" }), "Parallel", a.Descriptor, b.Descriptor);
    }

    /// <summary>Two lines are perpendicular.</summary>
    public ConstrainedSketch Perpendicular(SketchLineRef a, SketchLineRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Recorded(Add(new DirectionConstraint(a.P1, a.P2, b.P1, b.P2, parallel: false, 0)
            { Name = $"Perpendicular({a}, {b})" }), "Perpendicular", a.Descriptor, b.Descriptor);
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
        return Recorded(Add(new DirectionConstraint(a.P1, a.P2, b.P1, b.P2, parallel: false, Math.Cos(radians))
            { Name = $"Angle({a}, {b}) = {radians:g6} rad" }),
            "Angle", a.Descriptor, b.Descriptor, Num(radians));
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
            return Recorded(Add(new TangentAtJointConstraint(line.P1, line.P2, arc.Center, joint)
                { Name = name }), "Tangent", line.Descriptor, arc.Descriptor);

        var seed = _map.Seed;
        var p1 = SketchVariables.Point(seed, line.P1);
        var direction = SketchVariables.Point(seed, line.P2) - p1;
        double cross = direction.Cross(SketchVariables.Point(seed, arc.Center) - p1);
        double side = cross >= 0 ? 1 : -1;
        return Recorded(Add(new TangentLineArcConstraint(line.P1, line.P2, arc.Center, arc.Radius, side)
            { Name = name }), "Tangent", line.Descriptor, arc.Descriptor);
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
            return Recorded(Add(new DirectionConstraint(joint, a.Center, joint, b.Center, parallel: true, 0)
                { Name = name }), "Tangent", a.Descriptor, b.Descriptor);

        var seed = _map.Seed;
        double separation =
            (SketchVariables.Point(seed, a.Center) - SketchVariables.Point(seed, b.Center)).Length;
        double radiusA = seed[a.Radius], radiusB = seed[b.Radius];
        double innerSign = radiusA >= radiusB ? 1 : -1;
        bool external = Math.Abs(separation - (radiusA + radiusB))
            <= Math.Abs(separation - innerSign * (radiusA - radiusB));
        return Recorded(Add(new TangentArcArcConstraint(a.Center, a.Radius, b.Center, b.Radius, external, innerSign)
            { Name = name }), "Tangent", a.Descriptor, b.Descriptor);
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
        return Recorded(Add(new EqualLengthConstraint(a.P1, a.P2, b.P1, b.P2)
            { Name = $"EqualLength({a}, {b})" }), "EqualLength", a.Descriptor, b.Descriptor);
    }

    /// <summary>Two arcs have equal radii.</summary>
    public ConstrainedSketch EqualRadius(SketchArcRef a, SketchArcRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Recorded(Add(new EqualScalarConstraint(a.Radius, b.Radius)
            { Name = $"EqualRadius({a}, {b})" }), "EqualRadius", a.Descriptor, b.Descriptor);
    }

    /// <summary>Two arcs share a center.</summary>
    public ConstrainedSketch Concentric(SketchArcRef a, SketchArcRef b)
    {
        RequireOwned(a.Owner, a.Description);
        RequireOwned(b.Owner, b.Description);
        return Recorded(Add(new CoincidentConstraint(a.Center, b.Center)
            { Name = $"Concentric({a}, {b})" }), "Concentric", a.Descriptor, b.Descriptor);
    }

    /// <summary>Pins a point exactly where it was drawn — the sketch's datum. Without a
    /// fix (or dimensions to one), a sketch always keeps its rigid-body freedoms and
    /// can never reach zero remaining DOF.</summary>
    public ConstrainedSketch Fix(SketchPointRef point)
    {
        var owned = Owned(point);
        var seed = _map.Seed;
        return Recorded(Add(new FixConstraint(owned.Variable, seed[owned.Variable], seed[owned.Variable + 1])
            { Name = $"Fix({point})" }), "Fix", point.Descriptor);
    }

    /// <summary>Pins both endpoints of a line where they were drawn.</summary>
    public ConstrainedSketch Fix(SketchLineRef line)
    {
        RequireOwned(line.Owner, line.Description);
        var seed = _map.Seed;
        Add(new FixConstraint(line.P1, seed[line.P1], seed[line.P1 + 1])
            { Name = $"Fix({line} start)" });
        return Recorded(Add(new FixConstraint(line.P2, seed[line.P2], seed[line.P2 + 1])
            { Name = $"Fix({line} end)" }), "Fix", line.Descriptor);
    }

    /// <summary>Pins an arc's center and radius as drawn (its endpoints may still slide
    /// along it unless they are pinned too).</summary>
    public ConstrainedSketch Fix(SketchArcRef arc)
    {
        RequireOwned(arc.Owner, arc.Description);
        var seed = _map.Seed;
        Add(new FixConstraint(arc.Center, seed[arc.Center], seed[arc.Center + 1])
            { Name = $"Fix({arc} center)" });
        return Recorded(Add(new ScalarValueConstraint(arc.Radius, seed[arc.Radius])
            { Name = $"Fix({arc} radius)" }), "Fix", arc.Descriptor);
    }

    /// <summary>Dimension: the distance between two points is
    /// <paramref name="distance"/> (&gt; 0; for zero use <see cref="Coincident"/>,
    /// which stays differentiable at its own solution).</summary>
    public ConstrainedSketch Distance(SketchPointRef a, SketchPointRef b, double distance)
    {
        if (!(distance > 0))
            throw new ArgumentOutOfRangeException(nameof(distance),
                "Point-to-point distance must be positive; a zero distance is Coincident.");
        return Recorded(Add(new DistancePointsConstraint(Owned(a).Variable, Owned(b).Variable, distance)
            { Name = $"Distance({a}, {b}) = {distance:g6}" }),
            "Distance", a.Descriptor, b.Descriptor, Num(distance));
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
        return Recorded(Add(new DistancePointLineConstraint(owned.Variable, line.P1, line.P2, distance, side)
            { Name = $"Distance({point}, {line}) = {distance:g6}" }),
            "Distance", point.Descriptor, line.Descriptor, Num(distance));
    }

    /// <summary>
    /// The point lies ON the line's carrier — the sketcher's point-on-object.
    /// </summary>
    /// <remarks>
    /// <para>This is <see cref="Distance(SketchPointRef, SketchLineRef, double)"/> at zero,
    /// which is the *right* reduction rather than a convenience: the point-to-line residual
    /// is the SIGNED offset <c>ŝ·d̂ × (p − a)</c>, so it passes smoothly through zero and is
    /// first order there — unlike the point-to-POINT distance, whose zero is a cone point
    /// and which is therefore refused in favour of <see cref="Coincident"/>.</para>
    /// <para>The carrier is INFINITE: the point need not land between the line's own
    /// endpoints, which is what makes this useful for aligning geometry against a datum
    /// edge. Constrain the ends too if you mean the segment.</para>
    /// </remarks>
    public ConstrainedSketch PointOn(SketchPointRef point, SketchLineRef line)
    {
        var owned = Owned(point);
        RequireOwned(line.Owner, line.Description);
        // side = +1: at a zero target the two signs give the same solution set, and the
        // residual is smooth through it, so there is no branch to select.
        return Recorded(Add(new DistancePointLineConstraint(owned.Variable, line.P1, line.P2, 0, 1)
            { Name = $"PointOn({point}, {line})" }), "PointOn", point.Descriptor, line.Descriptor);
    }

    /// <summary>
    /// The point lies ON the arc's carrier circle — <c>|p − c| = r</c>, one row.
    /// </summary>
    /// <remarks>
    /// <para>The same residual the solver already applies internally to an arc's own two
    /// endpoints (<c>ArcEndpointConstraint</c>), so this is that rule reused rather than a
    /// second spelling of it — and it counts toward the rank like any other row.</para>
    /// <para>The carrier is the whole CIRCLE, not the drawn sweep: a point-on-object
    /// constraint that silently refused to let the point pass an arc's end would be a
    /// branch selector in disguise. Constrain the endpoints if the sweep matters.</para>
    /// <para>Refused when the point is drawn exactly AT the centre: the residual's gradient
    /// there is the undefined direction <c>(p − c)/|p − c|</c>, so the solve has no first
    /// order to work with — the stationary-configuration rule, named rather than nudged.</para>
    /// </remarks>
    public ConstrainedSketch PointOn(SketchPointRef point, SketchArcRef arc)
    {
        var owned = Owned(point);
        RequireOwned(arc.Owner, arc.Description);
        var seed = _map.Seed;
        var offset = SketchVariables.Point(seed, owned.Variable) - SketchVariables.Point(seed, arc.Center);
        // Relative to the drawn radius, so the guard is scale-free: an absolute epsilon
        // here would reject a legitimate micron-scale sketch and pass a metre-scale
        // coincidence.
        if (offset.Length <= 1e-9 * Math.Max(seed[arc.Radius], _map.CharacteristicLength))
            throw new ArgumentException(
                $"PointOn({point}, {arc}) is drawn with the point at the arc's centre, where the " +
                "residual |p - c| - r has no gradient direction; move the point off the centre first.");
        return Recorded(Add(new ArcEndpointConstraint(owned.Variable, arc.Center, arc.Radius)
            { Name = $"PointOn({point}, {arc})" }), "PointOn", point.Descriptor, arc.Descriptor);
    }

    /// <summary>
    /// The point lies on the CARRIER of a bézier or elliptical-arc segment — the whole
    /// ellipse, or the bézier's own cubic beyond its drawn stretch — for the same reason
    /// the line and arc overloads take carriers: clamping to the drawn run would be a
    /// branch selector in disguise.
    ///
    /// <para><b>The two kinds are different residuals, and the difference is real rather
    /// than a convenience.</b> An ellipse is a CONIC, so membership has a closed algebraic
    /// form — <c>|M⁻¹(p − C)| − 1</c>, the arc's own <c>|p − c| − r</c> with the radius
    /// replaced by the semi-axis matrix — and costs one row and no new unknown. A cubic
    /// bézier has no such form, so the foot parameter joins the system as a VARIABLE and
    /// the residual is <c>B(t) − p = 0</c>: two rows and one unknown, which removes exactly
    /// the one degree of freedom a point-on-curve constraint should.</para>
    ///
    /// <para>The DRAWN foot is the seed and the branch selector, the rule the whole layer
    /// runs on — a cubic can pass a point three times, and which crossing is meant is read
    /// off the drawing rather than guessed at.</para>
    /// </summary>
    public ConstrainedSketch PointOn(SketchPointRef point, SketchCurveRef curve)
    {
        var owned = Owned(point);
        RequireOwned(curve.Owner, curve.Description);
        var seed = _map.Seed;
        switch (curve.Segment)
        {
            case EllipseSeg ellipse:
            {
                // An ellipse with no chord is legal here — a full-ellipse loop's carrier is
                // simply FIXED, and the residual is still first order in the point.
                if (curve.Start >= 0 && curve.Start == curve.End)
                    throw new ArgumentException(
                        $"PointOn({point}, {curve}): {curve} closes on itself, so its two joints are one " +
                        "variable and it has no chord for its shape to ride.");
                var p = SketchVariables.Point(seed, owned.Variable);
                double reach = Math.Max(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length);
                // Scale-free, and the SAME refusal PointOn(point, arc) makes: at the
                // centre the residual has magnitude but no gradient direction.
                if ((p - ellipse.Center).Length <= 1e-9 * Math.Max(reach, _map.CharacteristicLength))
                    throw new ArgumentException(
                        $"PointOn({point}, {curve}) is drawn with the point at the ellipse's centre, " +
                        "where the residual has no gradient direction; move the point off the centre first.");
                return Recorded(Add(new PointOnEllipseConstraint(
                    owned.Variable, curve.Start, curve.End, ellipse.Start, ellipse.End,
                    ellipse.Center, ellipse.SemiAxisX, ellipse.SemiAxisY)
                    { Name = $"PointOn({point}, {curve})" }),
                    "PointOn", point.Descriptor, curve.Descriptor);
            }

            case CubicSeg cubic:
            {
                RequireChord(curve, $"PointOn({point}, {curve})");
                var p = SketchVariables.Point(seed, owned.Variable);
                int foot = AddAuxiliary(NearestParameter(cubic, p));
                return Recorded(Add(new PointOnBezierConstraint(
                    owned.Variable, curve.Start, curve.End, foot,
                    cubic.P0, cubic.P3, cubic.Control1, cubic.Control2)
                    { Name = $"PointOn({point}, {curve})" }),
                    "PointOn", point.Descriptor, curve.Descriptor);
            }

            default:
                throw new ArgumentException($"{curve} is not a bézier or elliptical arc.");
        }
    }

    /// <summary>
    /// The tangent DIRECTION at one END of a bézier or elliptical arc is parallel to
    /// <paramref name="line"/> — the tangency those two segment kinds lacked.
    /// <para>Both carriers ride the chord similarity, so their end tangent is a FIXED
    /// complex multiple of the live chord (a cubic leaves its start along 3(C₁ − P₀), an
    /// ellipse along its own derivative there), which makes this the ordinary two-direction
    /// row with the curve's constant folded in. Pass <paramref name="perpendicular"/> for
    /// the right-angle form.</para>
    /// </summary>
    public ConstrainedSketch Tangent(
        SketchCurveRef curve, SketchCurveEnd at, SketchLineRef line, bool perpendicular = false)
    {
        RequireOwned(curve.Owner, curve.Description);
        RequireOwned(line.Owner, line.Description);
        RequireChord(curve, $"Tangent({curve}, {at.ToString().ToLowerInvariant()}, {line})");
        var factor = EndTangentFactor(curve, at);
        return Recorded(Add(new CurveTangentConstraint(
            curve.Start, curve.End, factor, line.P1, line.P2, parallel: !perpendicular)
            { Name = $"Tangent({curve} {at.ToString().ToLowerInvariant()}, {line})" }),
            perpendicular
                ? ["TangentAtEnd", curve.Descriptor, at.ToString(), line.Descriptor, "perpendicular"]
                : ["TangentAtEnd", curve.Descriptor, at.ToString(), line.Descriptor]);
    }

    /// <summary>
    /// <paramref name="line"/> is tangent to an elliptical arc's CARRIER conic — the whole
    /// ellipse, exactly as <c>Tangent(line, arc)</c> is tangent to an arc's whole circle
    /// and <c>PointOn</c> is on a carrier rather than on the drawn stretch.
    ///
    /// <para>It needs no foot parameter: an ellipse is a conic, so the extreme signed
    /// distance from it to a line is <c>n̂·(C − q) ± |Mᵀn̂|</c> for the semi-axis matrix
    /// <c>M = [A B]</c>, and tangency is one extreme vanishing — ONE row, signed, first
    /// order, no new unknown, reducing to the circular form exactly when A and B are
    /// perpendicular and equal.</para>
    ///
    /// <para><b>Which of the two tangents is meant is read off the drawing</b> (the branch
    /// selector the whole layer runs on), so a line drawn THROUGH the ellipse's centre has
    /// no side and is refused by name — the same singularity, and the same treatment, as a
    /// point drawn at an arc's centre. A BÉZIER is refused too, and for a reason rather
    /// than a deferral: it has no closed algebraic support function, so its tangency needs
    /// the foot parameter as a variable, which is a different constraint shape (two rows
    /// over one unknown) and is filed.</para>
    /// </summary>
    public ConstrainedSketch Tangent(SketchLineRef line, SketchCurveRef curve)
    {
        RequireOwned(line.Owner, line.Description);
        RequireOwned(curve.Owner, curve.Description);
        string what = $"Tangent({line}, {curve})";
        if (curve.Segment is CubicSeg cubicSeg)
        {
            // A cubic has no closed-form support function, so its tangency takes the
            // FOOT parameter as a solver variable — the PointOnBezier shape plus one
            // row: B(t) lies on the line AND B'(t) is parallel to it, two rows over one
            // new unknown, removing exactly the one DOF a tangency means.
            RequireChord(curve, what);
            var seedNow = _map.Seed;
            var q1 = SketchVariables.Point(seedNow, line.P1);
            var q2 = SketchVariables.Point(seedNow, line.P2);
            var drawnDirection = q2 - q1;
            if (!(drawnDirection.LengthSquared > 0))
                throw new ArgumentException($"{what}: {line} is drawn with no length.");
            int cubicFoot = AddAuxiliary(
                TangencyParameter(cubicSeg, q1, drawnDirection / drawnDirection.Length));
            return Recorded(Add(new TangentLineBezierConstraint(
                line.P1, line.P2, curve.Start, curve.End, cubicFoot,
                cubicSeg.P0, cubicSeg.P3, cubicSeg.Control1, cubicSeg.Control2)
                { Name = what }), "Tangent", line.Descriptor, curve.Descriptor);
        }
        if (curve.Segment is not EllipseSeg ellipse)
            throw new ArgumentException(
                $"{what}: tangency is available for an elliptical arc or a cubic bézier; " +
                $"{curve} is neither.");
        // A full-ellipse loop's carrier is simply FIXED, which is legal: the row is still
        // first order in the line's own two points.
        if (curve.Start >= 0 && curve.Start == curve.End)
            throw new ArgumentException(
                $"{what}: {curve} closes on itself, so its two joints are one variable and it has no " +
                "chord for its shape to ride.");

        var seed = _map.Seed;
        var p1 = SketchVariables.Point(seed, line.P1);
        var direction = SketchVariables.Point(seed, line.P2) - p1;
        // The line is pulled into the DRAWN frame before its side is read, so the seed and
        // the residual answer the same question.
        var chord = curve.Start < 0
            ? default
            : SketchVariables.Point(seed, curve.End) - SketchVariables.Point(seed, curve.Start);
        var (u, v) = curve.Start < 0 || !(chord.LengthSquared > 0)
            ? (p1, p1 + direction)
            : (PullToDrawn(p1, seed, curve, chord, ellipse),
               PullToDrawn(p1 + direction, seed, curve, chord, ellipse));
        var pulled = v - u;
        if (!(pulled.LengthSquared > 0))
            throw new ArgumentException($"{what}: {line} is drawn with no length.");
        var unit = pulled / pulled.Length;
        double offset = (ellipse.Center - u).Cross(unit);
        double reach = Math.Max(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length);
        // Scale-free: at zero offset the two tangents coincide in the residual and the
        // drawing states no side, so the constraint has no branch to keep.
        if (Math.Abs(offset) <= 1e-9 * Math.Max(reach, _map.CharacteristicLength))
            throw new ArgumentException(
                $"{what} is drawn with the line through the ellipse's centre, where the two tangents " +
                "are indistinguishable and the drawing states no side; move the line off the centre first.");

        return Recorded(Add(new TangentLineEllipseConstraint(
            line.P1, line.P2, curve.Start, curve.End, ellipse.Start, ellipse.End,
            ellipse.Center, ellipse.SemiAxisX, ellipse.SemiAxisY, offset >= 0 ? 1 : -1)
            { Name = what }), "Tangent", line.Descriptor, curve.Descriptor);
    }

    private static Vector2d PullToDrawn(
        in Vector2d p, double[] seed, in SketchCurveRef curve, in Vector2d chord, EllipseSeg ellipse)
    {
        var s = SketchVariables.Point(seed, curve.Start);
        var zeta = PointOnEllipseConstraint.ComplexDivide(p - s, chord);
        return PointOnEllipseConstraint.ComplexMultiply(zeta, ellipse.End - ellipse.Start) + ellipse.Start;
    }

    /// <summary>
    /// A curve whose shape rides its chord needs a chord to ride, and there are two ways
    /// not to have one — a loop that is ONE closed curve carries no joints at all (a full
    /// ellipse), and a single-segment loop that is not recognized as closed has its two
    /// joints as one variable. Both are refused here rather than dividing by zero three
    /// stages down.
    /// </summary>
    private static void RequireChord(in SketchCurveRef curve, string what)
    {
        if (curve.Start < 0)
            throw new ArgumentException(
                $"{what}: a closed single-curve loop carries no joints, so its carrier is fixed and " +
                "the constraint could only move the other side.");
        if (curve.Start == curve.End)
            throw new ArgumentException(
                $"{what}: {curve} closes on itself, so its two joints are one variable and it has no " +
                "chord for its shape to ride.");
    }

    /// <summary>
    /// The end tangent expressed OVER the drawn chord, so multiplying it by the live chord
    /// gives the live tangent. A direction, so its sign and magnitude are free — which is
    /// why an elliptical arc's sweep sign never has to be reasoned about here.
    /// </summary>
    private static Vector2d EndTangentFactor(SketchCurveRef curve, SketchCurveEnd at)
    {
        switch (curve.Segment)
        {
            case CubicSeg cubic:
            {
                var chord = cubic.P3 - cubic.P0;
                var tangent = at == SketchCurveEnd.Start
                    ? cubic.Control1 - cubic.P0
                    : cubic.P3 - cubic.Control2;
                return Divide(tangent, chord, curve);
            }

            case EllipseSeg ellipse:
            {
                double angle = ellipse.StartAngle + (at == SketchCurveEnd.Start ? 0 : ellipse.Sweep);
                var tangent = ellipse.SemiAxisY * Math.Cos(angle) - ellipse.SemiAxisX * Math.Sin(angle);
                return Divide(tangent, ellipse.End - ellipse.Start, curve);
            }

            default:
                throw new ArgumentException($"{curve} is not a bézier or elliptical arc.");
        }
    }

    private static Vector2d Divide(in Vector2d value, in Vector2d chord, in SketchCurveRef curve)
    {
        // Exact-zero division guard: a chord-degenerate drawn curve has no similarity to
        // ride, so there is nothing to express the tangent over.
        if (!(chord.LengthSquared > 0))
            throw new ArgumentException(
                $"{curve} is drawn with coincident endpoints, so it has no chord for its shape to ride.");
        return PointOnEllipseConstraint.ComplexDivide(value, chord);
    }

    /// <summary>
    /// The drawn foot: a coarse scan plus a few Newton steps on <c>(B(t) − p)·B′(t)</c>.
    /// It is a SEED and a branch choice, never an answer — the solver refines it as an
    /// ordinary unknown — so the scan needs only to land in the right basin, and the
    /// parameter is deliberately not clamped to [0, 1].
    /// </summary>
    private static double NearestParameter(CubicSeg cubic, in Vector2d p)
    {
        const int samples = 64;
        double best = 0, bestDistance = double.PositiveInfinity;
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            double distance = (BezierPoint(cubic, t) - p).LengthSquared;
            if (distance < bestDistance)
                (bestDistance, best) = (distance, t);
        }
        for (int i = 0; i < 8; i++)
        {
            var delta = BezierPoint(cubic, best) - p;
            var first = BezierDerivative(cubic, best);
            var second = BezierSecondDerivative(cubic, best);
            double gradient = delta.Dot(first);
            double curvature = first.Dot(first) + delta.Dot(second);
            if (!(Math.Abs(curvature) > 0))
                break;
            best -= gradient / curvature;
        }
        return best;
    }

    /// <summary>
    /// The tangency foot the DRAWN configuration selects — the branch selector the whole
    /// layer runs on. The tangents of the CARRIER parallel to the drawn line are the real
    /// roots of <c>cross(B'(t), d̂) = 0</c>, a plain quadratic in t; among them the one
    /// whose point lies nearest the drawn line is the tangency the drawing means. With no
    /// real root (no carrier tangent runs the drawn way — the solve may still rotate the
    /// line) the seed falls back to a dense scan minimizing the combined residual.
    /// </summary>
    private static double TangencyParameter(CubicSeg cubic, Vector2d onLine, Vector2d unit)
    {
        // B'(t)/3 = (1−t)²·(C1−P0) + 2(1−t)t·(C2−C1) + t²·(P3−C2); crossing with the
        // unit direction gives a·t² + b·t + c for the three edge crosses.
        double q0 = (cubic.Control1 - cubic.P0).Cross(unit);
        double q1 = (cubic.Control2 - cubic.Control1).Cross(unit);
        double q2 = (cubic.P3 - cubic.Control2).Cross(unit);
        double a = q0 - 2 * q1 + q2;
        double b = 2 * (q1 - q0);
        double c = q0;

        double best = double.NaN, bestMiss = double.PositiveInfinity;
        void Consider(double t)
        {
            if (!double.IsFinite(t))
                return;
            double miss = Math.Abs((BezierPoint(cubic, t) - onLine).Cross(unit));
            if (miss < bestMiss)
                (bestMiss, best) = (miss, t);
        }
        if (Math.Abs(a) > 0)
        {
            double disc = b * b - 4 * a * c;
            if (disc >= 0)
            {
                double root = Math.Sqrt(disc);
                Consider((-b + root) / (2 * a));
                Consider((-b - root) / (2 * a));
            }
        }
        else if (Math.Abs(b) > 0)
        {
            Consider(-c / b);
        }
        if (double.IsFinite(best))
            return best;

        // No parallel tangent on the carrier: scan for the least-violating start.
        const int samples = 96;
        for (int i = 0; i <= samples; i++)
        {
            double t = -0.5 + 2.0 * i / samples;
            double r0 = (BezierPoint(cubic, t) - onLine).Cross(unit);
            double r1 = BezierDerivative(cubic, t).Cross(unit);
            double miss = r0 * r0 + r1 * r1;
            if (miss < bestMiss)
                (bestMiss, best) = (miss, t);
        }
        return best;
    }

    private static Vector2d BezierPoint(CubicSeg c, double t)
    {
        double u = 1 - t;
        return c.P0 * (u * u * u) + c.Control1 * (3 * u * u * t)
            + c.Control2 * (3 * u * t * t) + c.P3 * (t * t * t);
    }

    private static Vector2d BezierDerivative(CubicSeg c, double t)
    {
        double u = 1 - t;
        return (c.Control1 - c.P0) * (3 * u * u) + (c.Control2 - c.Control1) * (6 * u * t)
            + (c.P3 - c.Control2) * (3 * t * t);
    }

    private static Vector2d BezierSecondDerivative(CubicSeg c, double t) =>
        (c.Control2 - c.Control1 * 2 + c.P0) * (6 * (1 - t))
        + (c.P3 - c.Control2 * 2 + c.Control1) * (6 * t);

    /// <summary>Dimension: the arc's radius is <paramref name="radius"/> (&gt; 0).</summary>
    public ConstrainedSketch Radius(SketchArcRef arc, double radius)
    {
        RequireOwned(arc.Owner, arc.Description);
        if (!(radius > 0))
            throw new ArgumentOutOfRangeException(nameof(radius));
        return Recorded(Add(new ScalarValueConstraint(arc.Radius, radius)
            { Name = $"Radius({arc}) = {radius:g6}" }), "Radius", arc.Descriptor, Num(radius));
    }

    /// <summary>Dimension: the arc's diameter is <paramref name="diameter"/> (&gt; 0).</summary>
    public ConstrainedSketch Diameter(SketchArcRef arc, double diameter)
    {
        RequireOwned(arc.Owner, arc.Description);
        if (!(diameter > 0))
            throw new ArgumentOutOfRangeException(nameof(diameter));
        return Recorded(Add(new ScalarValueConstraint(arc.Radius, diameter / 2)
            { Name = $"Diameter({arc}) = {diameter:g6}" }), "Diameter", arc.Descriptor, Num(diameter));
    }

    // ============================================================== persistence

    /// <summary>
    /// The constraint declarations as JSON — an array of token records, one per public
    /// constraint call in the order made, each holding the method, its entity refs as
    /// canonical descriptors (<c>point(3)</c>, <c>holeLine(0,0)</c>,
    /// <c>centerOf(arc(2))</c>) and its numeric values in round-trippable form.
    /// <para><see cref="LoadConstraints"/> REPLAYS the records through the same public
    /// methods against the same drawn sketch, so the loaded system is the built one by
    /// construction (branch selectors and seeds re-derive from the same drawing) and
    /// save → load → save is a byte fixed point. The whole vocabulary is data — there
    /// is no lambda anywhere in it — so nothing loads as a warning.</para>
    /// </summary>
    public string SaveConstraints() =>
        System.Text.Json.JsonSerializer.Serialize(_records, MateSet.JsonOptions);

    /// <summary>
    /// Rebuilds a constrained sketch from <see cref="SaveConstraints"/>' JSON against
    /// the SAME drawn sketch — the drawing is the seed and every branch selector, so
    /// the sketch is part of the document (its curves already round-trip through
    /// <c>Sketch.ToCurves</c>/<c>FromCurves</c>) and the constraints ride beside it.
    /// An unknown method or an unparseable ref refuses BY NAME: a record this reader
    /// cannot replay is a file from a newer vocabulary, not something to skip silently.
    /// </summary>
    public static ConstrainedSketch LoadConstraints(Sketch sketch, string json)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var records = System.Text.Json.JsonSerializer.Deserialize<string[][]>(json)
            ?? throw new ArgumentException("The constraint JSON holds no records.", nameof(json));
        var cs = sketch.Constrain();
        foreach (var record in records)
            cs.Replay(record);
        return cs;
    }

    /// <summary>Every record method <see cref="Replay"/> understands — what the
    /// coverage test holds the public vocabulary against, so a new constraint method
    /// added without a replay arm fails a test rather than taking a document down.</summary>
    internal static readonly IReadOnlyList<string> SupportedRecordMethods =
    [
        "Coincident", "Horizontal", "Vertical", "Parallel", "Perpendicular", "Angle",
        "Tangent", "TangentAtEnd", "EqualLength", "EqualRadius", "Concentric", "Fix",
        "Distance", "PointOn", "Radius", "Diameter",
    ];

    private void Replay(string[] record)
    {
        if (record.Length == 0)
            throw new ArgumentException("A constraint record carries no method token.");
        string method = record[0];
        switch (method)
        {
            case "Coincident":
                Coincident(ParsePoint(record[1]), ParsePoint(record[2]));
                return;
            case "Horizontal":
                if (record.Length == 2) Horizontal(ParseLine(record[1]));
                else Horizontal(ParsePoint(record[1]), ParsePoint(record[2]));
                return;
            case "Vertical":
                if (record.Length == 2) Vertical(ParseLine(record[1]));
                else Vertical(ParsePoint(record[1]), ParsePoint(record[2]));
                return;
            case "Parallel":
                Parallel(ParseLine(record[1]), ParseLine(record[2]));
                return;
            case "Perpendicular":
                Perpendicular(ParseLine(record[1]), ParseLine(record[2]));
                return;
            case "Angle":
                Angle(ParseLine(record[1]), ParseLine(record[2]), ParseNumber(record[3]));
                return;
            case "Tangent":
                // The overload is the TOKEN TYPES' to say (the record stays one method).
                if (IsKind(record[1], "line") && IsKind(record[2], "arc"))
                    Tangent(ParseLine(record[1]), ParseArc(record[2]));
                else if (IsKind(record[1], "arc"))
                    Tangent(ParseArc(record[1]), ParseArc(record[2]));
                else
                    Tangent(ParseLine(record[1]), ParseCurve(record[2]));
                return;
            case "TangentAtEnd":
                Tangent(ParseCurve(record[1]), Enum.Parse<SketchCurveEnd>(record[2]),
                    ParseLine(record[3]), perpendicular: record.Length == 5);
                return;
            case "EqualLength":
                EqualLength(ParseLine(record[1]), ParseLine(record[2]));
                return;
            case "EqualRadius":
                EqualRadius(ParseArc(record[1]), ParseArc(record[2]));
                return;
            case "Concentric":
                Concentric(ParseArc(record[1]), ParseArc(record[2]));
                return;
            case "Fix":
                if (IsKind(record[1], "line")) Fix(ParseLine(record[1]));
                else if (IsKind(record[1], "arc")) Fix(ParseArc(record[1]));
                else Fix(ParsePoint(record[1]));
                return;
            case "Distance":
                if (IsKind(record[2], "line"))
                    Distance(ParsePoint(record[1]), ParseLine(record[2]), ParseNumber(record[3]));
                else
                    Distance(ParsePoint(record[1]), ParsePoint(record[2]), ParseNumber(record[3]));
                return;
            case "PointOn":
                if (IsKind(record[2], "line")) PointOn(ParsePoint(record[1]), ParseLine(record[2]));
                else if (IsKind(record[2], "arc")) PointOn(ParsePoint(record[1]), ParseArc(record[2]));
                else PointOn(ParsePoint(record[1]), ParseCurve(record[2]));
                return;
            case "Radius":
                Radius(ParseArc(record[1]), ParseNumber(record[2]));
                return;
            case "Diameter":
                Diameter(ParseArc(record[1]), ParseNumber(record[2]));
                return;
            default:
                throw new ArgumentException(
                    $"Unknown constraint record '{method}' — this file uses a constraint " +
                    "vocabulary this reader does not know.");
        }
    }

    private static bool IsKind(string token, string kind)
    {
        var (name, _) = ParseTerm(token);
        return name.Equals(kind, StringComparison.OrdinalIgnoreCase)
            || name.Equals(
                "hole" + char.ToUpperInvariant(kind[0]) + kind[1..], StringComparison.Ordinal);
    }

    private static (string Name, string Inner) ParseTerm(string token)
    {
        int open = token.IndexOf('(');
        if (open <= 0 || !token.EndsWith(')'))
            throw new ArgumentException($"'{token}' is not a constraint entity descriptor.");
        return (token[..open], token[(open + 1)..^1]);
    }

    private static int[] ParseIndices(string inner, int count, string token)
    {
        var parts = inner.Split(',');
        if (parts.Length != count)
            throw new ArgumentException($"'{token}' should carry {count} index(es).");
        var indices = new int[count];
        for (int i = 0; i < count; i++)
            indices[i] = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        return indices;
    }

    private static double ParseNumber(string token) =>
        double.Parse(token, System.Globalization.CultureInfo.InvariantCulture);

    private SketchPointRef ParsePoint(string token)
    {
        var (name, inner) = ParseTerm(token);
        return name switch
        {
            "point" => Point(ParseIndices(inner, 1, token)[0]),
            "holePoint" => HolePoint(
                ParseIndices(inner, 2, token)[0], ParseIndices(inner, 2, token)[1]),
            "centerOf" => CenterOf(ParseArc(inner)),
            _ => throw new ArgumentException($"'{token}' is not a point descriptor."),
        };
    }

    private SketchLineRef ParseLine(string token)
    {
        var (name, inner) = ParseTerm(token);
        return name switch
        {
            "line" => Line(ParseIndices(inner, 1, token)[0]),
            "holeLine" => HoleLine(
                ParseIndices(inner, 2, token)[0], ParseIndices(inner, 2, token)[1]),
            _ => throw new ArgumentException($"'{token}' is not a line descriptor."),
        };
    }

    private SketchArcRef ParseArc(string token)
    {
        var (name, inner) = ParseTerm(token);
        return name switch
        {
            "arc" => Arc(ParseIndices(inner, 1, token)[0]),
            "holeArc" => HoleArc(
                ParseIndices(inner, 2, token)[0], ParseIndices(inner, 2, token)[1]),
            _ => throw new ArgumentException($"'{token}' is not an arc descriptor."),
        };
    }

    private SketchCurveRef ParseCurve(string token)
    {
        var (name, inner) = ParseTerm(token);
        return name switch
        {
            "curve" => Curve(ParseIndices(inner, 1, token)[0]),
            "holeCurve" => HoleCurve(
                ParseIndices(inner, 2, token)[0], ParseIndices(inner, 2, token)[1]),
            _ => throw new ArgumentException($"'{token}' is not a curve descriptor."),
        };
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
            diagnostics.Add(FreedomNote(TotalVariables));
            return new SketchSolveResult(true, 0, 0, TotalVariables, 0, 0, diagnostics)
                { Sketch = Source };
        }

        var system = new List<SketchConstraint>(_constraints);
        AddInternalConstraints(system);

        var outcome = SketchLevenberg.Run(SolverSeed(), system, _map.CharacteristicLength, settings);
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
            TotalVariables, outcome.Rank, redundant, diagnostics)
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

    /// <summary>The canonical descriptor term an accessor stamps on its ref — the outer
    /// loop's spelling for loop 0, the hole spelling (hole = loop − 1) otherwise.</summary>
    private static string RefDescriptor(string outer, string hole, int loop, int index) =>
        loop == 0 ? $"{outer}({index})" : $"{hole}({loop - 1},{index})";

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
        return new SketchPointRef(this, map.JointVars[joint], $"{LoopName(loop)} point {joint}",
            RefDescriptor("point", "holePoint", loop, joint));
    }

    private SketchLineRef LineRef(int loop, int segment)
    {
        var map = _map.Loops[loop];
        RequireSegment(loop, segment);
        return map.Segments[segment] switch
        {
            LineSeg => new SketchLineRef(this,
                map.JointVars[segment], map.JointVars[(segment + 1) % map.JointCount],
                $"{LoopName(loop)} line {segment}",
                RefDescriptor("line", "holeLine", loop, segment)),
            ArcSeg => throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is an arc — use Arc({segment})."),
            var other => throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is {KindName(other)}; only its endpoint " +
                "joints (Point) can be constrained."),
        };
    }

    /// <summary>The segment kind as a caller would say it — so a refusal names what the
    /// segment IS rather than assuming everything unrecognized is a bézier.</summary>
    private static string KindName(SketchSegment segment) => segment switch
    {
        LineSeg => "a line",
        ArcSeg => "an arc",
        EllipseSeg => "an elliptical arc",
        _ => "a bézier",
    };

    private SketchArcRef ArcRef(int loop, int segment)
    {
        var map = _map.Loops[loop];
        RequireSegment(loop, segment);
        if (map.CenterVars[segment] < 0)
            throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is not an arc " +
                $"(it is {KindName(map.Segments[segment])}).");
        bool circle = map.Segments[segment] is ArcSeg { IsFullCircle: true };
        int startJoint = map.SingleCircle ? -1 : map.JointVars[segment];
        int endJoint = map.SingleCircle ? -1 : map.JointVars[(segment + 1) % map.JointCount];
        return new SketchArcRef(this, map.CenterVars[segment], map.RadiusVars[segment],
            startJoint, endJoint, $"{LoopName(loop)} {(circle ? "circle" : "arc")} {segment}",
            RefDescriptor("arc", "holeArc", loop, segment));
    }

    private SketchCurveRef CurveRef(int loop, int segment)
    {
        var map = _map.Loops[loop];
        RequireSegment(loop, segment);
        var drawn = map.Segments[segment];
        if (drawn is not (CubicSeg or EllipseSeg))
            throw new ArgumentException(
                $"Segment {segment} of the {LoopName(loop)} loop is {KindName(drawn)}; Curve() is for " +
                "béziers and elliptical arcs (use Line or Arc).");
        // A loop that is ONE closed curve has no joints at all — the same structural case
        // a full circle is — so its carrier is fixed and the ref says so with −1.
        int start = map.SingleCircle ? -1 : map.JointVars[segment];
        int end = map.SingleCircle ? -1 : map.JointVars[(segment + 1) % map.JointCount];
        string kind = drawn is CubicSeg ? "bézier" : "ellipse";
        return new SketchCurveRef(this, start, end, drawn, $"{LoopName(loop)} {kind} {segment}",
            RefDescriptor("curve", "holeCurve", loop, segment));
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
