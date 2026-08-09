using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Ecad;

public sealed partial class PcbLayout
{
    /// <summary>Begins a set of placement constraints over this layout (chainable). The layout
    /// is the SEED — every constraint's branch is read from the drawn poses, and a failed solve
    /// leaves this layout bit-identically unchanged (the solve produces a NEW solved layout on
    /// success, never mutating the source).</summary>
    public ConstrainedLayout Constrain() => new(this);
}

/// <summary>Knobs for <see cref="ConstrainedLayout.Solve"/>.</summary>
public sealed class PcbConstraintSolverSettings
{
    /// <summary>Convergence tolerance, in board millimetres. Every residual is a LENGTH (angular
    /// residuals are scaled by the board diagonal), so the default is the 1e-9 absolute weld
    /// tier — the poses a solve produces must weld with the copper the layout derives.</summary>
    public double Tolerance { get; init; } = EngrCAD.Core.Tolerance.Default.Linear;

    /// <summary>Maximum Levenberg–Marquardt iterations before the solve is declared failed.</summary>
    public int MaxIterations { get; init; } = 200;

    /// <summary>When true, a solve that converges but leaves free degrees of freedom is reported
    /// as a failure (<see cref="ConstrainedLayout.Solve"/> throws). Off by default: an
    /// under-constrained layout is legitimate, and the free-DOF count is always reported.</summary>
    public bool RequireFullyConstrained { get; init; }
}

/// <summary>What a placement constraint solve did, and what it could not do — the MateSolver
/// report, in the PCB domain.</summary>
/// <param name="Converged">Every residual fell below the tolerance.</param>
/// <param name="Iterations">Levenberg–Marquardt steps taken.</param>
/// <param name="Residual">The largest remaining residual, in board millimetres.</param>
/// <param name="FreeDegreesOfFreedom">3 × the number of movable placement bodies (a grounded
/// group, and a placement no constraint mentions, are not counted).</param>
/// <param name="ConstrainedDegreesOfFreedom">The rank of the constraint Jacobian at the
/// solution — how many of those degrees of freedom the constraints actually pin.</param>
/// <param name="Diagnostics">Human-readable notes: which constraints are worst, which
/// placements no constraint mentions, what is still free.</param>
/// <param name="Solved">The solved layout — a NEW <see cref="PcbLayout"/> at the solved poses,
/// non-null only when <paramref name="Converged"/>. The pads, copper and nets DERIVE from it, so
/// <c>Solved.Check()</c> still passes and <c>Solved.PadsOfNet</c> returns the moved copper.</param>
public sealed record PcbConstraintSolveResult(
    bool Converged,
    int Iterations,
    double Residual,
    int FreeDegreesOfFreedom,
    int ConstrainedDegreesOfFreedom,
    IReadOnlyList<string> Diagnostics,
    PcbLayout? Solved)
{
    /// <summary>Degrees of freedom the constraints leave open (0 = fully constrained).</summary>
    public int RemainingDegreesOfFreedom => FreeDegreesOfFreedom - ConstrainedDegreesOfFreedom;

    /// <summary>True when the constraints do not pin the placement completely. Not an error by
    /// itself — see <see cref="PcbConstraintSolverSettings.RequireFullyConstrained"/>.</summary>
    public bool IsUnderConstrained => RemainingDegreesOfFreedom > 0;

    /// <summary>True when the solve did not converge — usually genuinely contradictory
    /// constraints (over-constrained), occasionally a start pose too far from any solution.
    /// <see cref="Diagnostics"/> names the constraints carrying the residual.</summary>
    public bool IsOverConstrained => !Converged;

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Converged ? "solved" : "FAILED")
            .Append($" in {Iterations} iteration{(Iterations == 1 ? "" : "s")}; ")
            .Append($"worst residual {Residual:g3}; ")
            .Append($"{ConstrainedDegreesOfFreedom} of {FreeDegreesOfFreedom} DOF constrained");
        if (RemainingDegreesOfFreedom > 0)
            text.Append($" ({RemainingDegreesOfFreedom} free)");
        foreach (string note in Diagnostics)
            text.Append("\n  · ").Append(note);
        return text.ToString();
    }
}

/// <summary>A placement solve that could not be satisfied; carries the full report.</summary>
public sealed class PcbConstraintSolveException(PcbConstraintSolveResult result)
    : InvalidOperationException($"The placement constraints could not be solved.\n{result}")
{
    /// <summary>The failed solve's report.</summary>
    public PcbConstraintSolveResult Result { get; } = result;
}

/// <summary>
/// A <see cref="PcbLayout"/> plus a set of placement constraints — the PCB-domain analogue of
/// <c>ConstrainedSketch</c> / <c>MateSet</c>, feeding a focused Levenberg–Marquardt solver
/// (<see cref="PcbConstraintSolver"/>) that follows the MateSolver doctrine exactly.
///
/// <para><b>The variables</b> are each free placement's rigid 2D pose <c>(x, y, θ)</c> on the
/// board. A <see cref="Lock(string)"/> pins a placement (a datum); a <see cref="Group(string[])"/>
/// ties several placements into one rigid body that moves together. Everything else is a residual:
/// distances, alignments, edge-flush, parallel/perpendicular, containment and clearance.</para>
///
/// <para><b>Honesty rules</b> (the MateSolver doctrine): the drawn layout is the seed AND the
/// branch selector; an under-constrained layout is normal and reports its remaining DOF; a
/// contradiction and a stationary configuration are NAMED; and a failed solve leaves this layout
/// bit-identically unchanged — the solve produces a NEW solved layout on success.</para>
///
/// <code>
/// var result = layout.Constrain()
///     .Lock("U1")                                   // a datum
///     .Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 5)
///     .AlignY(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"))
///     .ClearOf("R1", "R2", 1.0)
///     .Solve();
/// var moved = result.Solved;   // pads/nets follow the solved poses
/// </code>
/// </summary>
public sealed partial class ConstrainedLayout
{
    private readonly List<PcbConstraint> _constraints = [];

    internal ConstrainedLayout(PcbLayout layout) => Layout = layout;

    internal ConstrainedLayout(PcbLayout layout, IEnumerable<PcbConstraint> constraints)
    {
        Layout = layout;
        foreach (var constraint in constraints)
        {
            foreach (var reference in constraint.Placements)
                if (!layout.Placements.Any(p => p.Reference == reference))
                    throw new FormatException(
                        $"A saved constraint names '{reference}', which the layout does not place.");
            _constraints.Add(constraint);
        }
    }

    /// <summary>The layout being constrained (the seed; never mutated by a solve).</summary>
    public PcbLayout Layout { get; }

    /// <summary>The constraints, in declaration order.</summary>
    public IReadOnlyList<PcbConstraint> Constraints => _constraints;

    private ConstrainedLayout Add(PcbConstraint constraint)
    {
        foreach (var reference in constraint.Placements)
            RequirePlaced(reference);
        _constraints.Add(constraint);
        return this;
    }

    private void RequirePlaced(string reference)
    {
        if (!Layout.Placements.Any(p => p.Reference == reference))
            throw new ArgumentException(
                $"'{reference}' is not placed in this layout — a constraint must name a placed "
                + "component.", nameof(reference));
    }

    // ---- structural ---------------------------------------------------------

    /// <summary>Pins a placement in place — a datum (chainable). The same as
    /// <see cref="Lock(string)"/>.</summary>
    public ConstrainedLayout Fix(string reference) => Lock(reference);

    /// <summary>Pins a placement in place — a datum (chainable).</summary>
    public ConstrainedLayout Lock(string reference) => Add(new LockConstraint(reference));

    /// <summary>Locks the relative poses of several placements into one rigid body — a functional
    /// block moved as a unit (chainable). At least two references.</summary>
    public ConstrainedLayout Group(params string[] references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Length < 2)
            throw new ArgumentException("A group needs at least two placements.", nameof(references));
        return Add(new GroupConstraint([.. references]));
    }

    /// <summary>Locks the relative poses of several placements into one rigid body (the same as
    /// <see cref="Group(string[])"/>).</summary>
    public ConstrainedLayout Cluster(params string[] references) => Group(references);

    // ---- rotation -----------------------------------------------------------

    /// <summary>Pins a placement's rotation to the angle it was drawn at (chainable).</summary>
    public ConstrainedLayout FixRotation(string reference) =>
        Add(new FixRotationConstraint(reference));

    /// <summary>Pins a placement's rotation to an absolute angle in degrees (chainable).</summary>
    public ConstrainedLayout Orient(string reference, double degrees) =>
        Add(new OrientConstraint(reference, degrees));

    // ---- points -------------------------------------------------------------

    /// <summary>Two points coincide (chainable).</summary>
    public ConstrainedLayout Coincident(PlacementPoint a, PlacementPoint b) =>
        Add(new CoincidentConstraint(a, b));

    /// <summary>Two points a stated distance apart (chainable).</summary>
    public ConstrainedLayout Distance(PlacementPoint a, PlacementPoint b, double gap) =>
        Add(new DistanceConstraint(a, b, gap));

    /// <summary>A stated gap between two points — the same as <see cref="Distance"/>.</summary>
    public ConstrainedLayout Spacing(PlacementPoint a, PlacementPoint b, double gap) =>
        Distance(a, b, gap);

    /// <summary>Two points share an x coordinate (a column of parts) (chainable).</summary>
    public ConstrainedLayout AlignX(PlacementPoint a, PlacementPoint b) =>
        Add(new AlignConstraint(a, b, shareX: true));

    /// <summary>Two points share a y coordinate (a row of parts) (chainable).</summary>
    public ConstrainedLayout AlignY(PlacementPoint a, PlacementPoint b) =>
        Add(new AlignConstraint(a, b, shareX: false));

    // ---- directions & lines -------------------------------------------------

    /// <summary>Two directions held parallel (chainable).</summary>
    public ConstrainedLayout Parallel(PlacementDirection a, PlacementDirection b) =>
        Add(new DirectionPairConstraint(a, b, parallel: true));

    /// <summary>Two directions held perpendicular (chainable).</summary>
    public ConstrainedLayout Perpendicular(PlacementDirection a, PlacementDirection b) =>
        Add(new DirectionPairConstraint(a, b, parallel: false));

    /// <summary>A point lies on a line's carrier at a signed offset (0 = on the line). The
    /// residual is SIGNED (the point-on-line-is-distance-at-zero rule), so it stays first order
    /// through its own solution (chainable).</summary>
    public ConstrainedLayout PointOnLine(PlacementPoint point, PcbLine line, double offset = 0) =>
        Add(new PointOnLineConstraint(point, line.Point, line.Direction, offset));

    /// <summary>A stated gap between a point and a line (a component and a board edge), measured
    /// on the DRAWN side — the SIGNED spacing that stays first order (chainable).</summary>
    public ConstrainedLayout SpacingToEdge(PlacementPoint point, PcbLine edge, double gap)
    {
        double side = DrawnSideOfLine(point, edge);
        return Add(new PointOnLineConstraint(point, edge.Point, edge.Direction, side * gap));
    }

    /// <summary>A component's edge flush (or at a stated gap) to another edge — parallel + on-line
    /// in one constraint. The gap is measured on the DRAWN side (chainable).</summary>
    public ConstrainedLayout AlignEdge(PcbLine componentEdge, PcbLine targetEdge, double gap = 0)
    {
        double side = gap == 0 ? 1 : DrawnSideOfLine(componentEdge.Point, targetEdge);
        return Add(new AlignEdgeConstraint(componentEdge, targetEdge, gap, side));
    }

    // ---- containment & clearance --------------------------------------------

    /// <summary>A component's footprint stays inside a board region (a simple polygon, no
    /// repeated closing point), with an optional inner margin. Modelled by the footprint's
    /// bounding circle about its origin, so containment of the circle implies containment of the
    /// pads (chainable).</summary>
    public ConstrainedLayout InsideRegion(
        string reference, IEnumerable<Vector2d> polygon, double margin = 0) =>
        Add(new InsideRegionConstraint(reference, PcbGeometry.CanonicalLoop(polygon), margin));

    /// <summary>A component's footprint stays inside the board outline, with an optional inner
    /// margin (chainable).</summary>
    public ConstrainedLayout InsideBoard(string reference, double margin = 0) =>
        Add(new InsideRegionConstraint(reference, Layout.Board.OutlinePoints, margin));

    /// <summary>Two component footprints stay at least a distance apart, modelled by their
    /// bounding circles — a one-sided (active-set) clearance (chainable).</summary>
    public ConstrainedLayout ClearOf(string a, string b, double distance) =>
        Add(new ClearOfConstraint(a, b, distance));

    /// <summary>A component footprint stays at least a distance clear of a keep-out region — a
    /// one-sided (active-set) clearance (chainable).</summary>
    public ConstrainedLayout ClearOfRegion(
        string reference, IEnumerable<Vector2d> polygon, double distance) =>
        Add(new ClearOfRegionConstraint(reference, PcbGeometry.CanonicalLoop(polygon), distance));

    /// <summary>A component footprint stays at least a distance clear of a named board keep-out
    /// (chainable).</summary>
    public ConstrainedLayout ClearOfKeepOut(string reference, KeepOut keepOut, double distance)
    {
        ArgumentNullException.ThrowIfNull(keepOut);
        return Add(new ClearOfRegionConstraint(reference, keepOut.Polygon, distance));
    }

    // ---- reference helpers --------------------------------------------------

    /// <summary>A footprint pad's centre as a placement point (resolved against the layout's
    /// footprint now, so the constraint carries a plain local point).</summary>
    /// <exception cref="ArgumentException">The component has no footprint, or no pad of that
    /// number — refused by name.</exception>
    public PlacementPoint Pad(string reference, string padNumber)
    {
        RequirePlaced(reference);
        var footprint = Layout.Schematic.Find(reference)!.Definition.Footprint
            ?? throw new ArgumentException(
                $"'{reference}' has no footprint, so it has no pad '{padNumber}'.", nameof(padNumber));
        foreach (var pad in footprint.Pads)
            if (pad.Number == padNumber)
                return PlacementPoint.At(reference, pad.Center);
        throw new ArgumentException(
            $"'{reference}' ({footprint.Name}) has no pad '{padNumber}'. Its pads are: "
            + string.Join(", ", footprint.Pads.Select(p => p.Number)) + ".", nameof(padNumber));
    }

    /// <summary>The board outline's edge <paramref name="index"/> as a line (from outline point
    /// <paramref name="index"/> to the next, wrapping at the end).</summary>
    public PcbLine BoardEdge(int index)
    {
        var outline = Layout.Board.OutlinePoints;
        if (index < 0 || index >= outline.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"The board outline has {outline.Count} edges (0..{outline.Count - 1}).");
        return PcbLine.Board(outline[index], outline[(index + 1) % outline.Count]);
    }

    // ---- solving ------------------------------------------------------------

    /// <summary>Solves the constraints and returns the report. On convergence
    /// <see cref="PcbConstraintSolveResult.Solved"/> is a NEW layout at the solved poses; on
    /// failure it is null and this layout is untouched. Never throws.</summary>
    public PcbConstraintSolveResult TrySolve(PcbConstraintSolverSettings? settings = null) =>
        PcbConstraintSolver.Solve(Layout, _constraints, settings ?? new PcbConstraintSolverSettings());

    /// <summary>Solves the constraints, throwing <see cref="PcbConstraintSolveException"/> when
    /// they cannot be satisfied (or, with <see cref="PcbConstraintSolverSettings.RequireFullyConstrained"/>,
    /// when degrees of freedom remain). On success returns the report whose
    /// <see cref="PcbConstraintSolveResult.Solved"/> is the solved layout.</summary>
    public PcbConstraintSolveResult Solve(PcbConstraintSolverSettings? settings = null)
    {
        settings ??= new PcbConstraintSolverSettings();
        var result = TrySolve(settings);
        if (!result.Converged || (settings.RequireFullyConstrained && result.IsUnderConstrained))
            throw new PcbConstraintSolveException(result);
        return result;
    }

    /// <summary>The drawn-configuration side of <paramref name="point"/> against
    /// <paramref name="line"/> — the branch selector for a signed offset (±1; +1 when on the
    /// line, so a zero drawn distance takes the +side).</summary>
    private double DrawnSideOfLine(in PlacementPoint point, in PcbLine line)
    {
        var poses = PcbConstraintSolver.DrawnPoses(Layout);
        var p = PcbConstraintSolver.WorldPoint(poses, point);
        var q = PcbConstraintSolver.WorldPoint(poses, line.Point);
        var d = PcbConstraintSolver.WorldDirection(poses, line.Direction);
        double signed = d.Cross(p - q);   // >0 left of the line's direction
        return signed >= 0 ? 1 : -1;
    }
}
