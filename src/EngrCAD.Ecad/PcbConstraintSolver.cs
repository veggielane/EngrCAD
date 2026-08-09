using EngrCAD.Core;

namespace EngrCAD.Ecad;

/// <summary>
/// The focused Levenberg–Marquardt solver behind <see cref="ConstrainedLayout"/> — the
/// MateSolver doctrine applied to rigid 2D placement poses <c>(x, y, θ)</c>.
///
/// <para><b>Why a focused solver rather than the Modeling one.</b> The sketch and mate LM
/// engines are internal/private to <c>EngrCAD.Modeling</c> and bound to their own variable
/// models — the mate solver to 3D 6-DOF rigid perturbations of <c>Occurrence</c> frames, the
/// sketch solver to free 2D point coordinates. A PCB placement is a rigid 2D pose whose rotation
/// moves the WHOLE footprint about the placement origin, which is neither: not free points (the
/// footprint is rigid), not 3D. So this is the MateSolver doctrine rebuilt at 2D: an analytic
/// Jacobian; angular residuals scaled by the board diagonal so every residual is a length and one
/// linear tolerance is meaningful; the rotation variable divided by that length so every column is
/// O(1); rank from a diagonally pivoted Cholesky of JᵀJ at the 1e-6 relative floor (the
/// sketch-constraint floor, not the mate 1e-8 — at these small sizes 1e-8² sits below elimination
/// round-off); the drawn layout as seed AND branch selector; a failed solve leaving the source
/// bit-identically unchanged (a fresh layout is produced only on success).</para>
///
/// <para><b>The rigid-body model.</b> Each placement belongs to one rigid BODY — a singleton by
/// default, or several placements a <c>Group</c> ties together. A free body carries three
/// variables (its pose); each member carries a fixed offset from the body frame, captured from the
/// drawn layout, so a singleton reproduces the placement's own pose exactly and a group moves as
/// one. A <c>Lock</c> grounds a body.</para>
/// </summary>
internal sealed class PcbConstraintSolver
{
    /// <summary>Rank threshold on singular values, relative to the largest — dimensionless, so
    /// outside the linear tolerance ladder. Applied SQUARED (the pivots of JᵀJ are squared
    /// singular values). 1e-6 (not the mate solver's 1e-8) because 1e-8² sits below pivoted
    /// elimination's own round-off at layout sizes — the recorded sketch-constraint lesson.</summary>
    private const double RankRelativeTolerance = 1e-6;

    private readonly PcbLayout _layout;
    private readonly IReadOnlyList<PcbConstraint> _constraints;
    private readonly PcbConstraintSolverSettings _settings;

    // rigid-body decomposition
    private sealed class Body
    {
        public required List<string> Members { get; init; }   // placement refs, in layout order
        public bool Grounded { get; set; }
        public int Block { get; set; } = -1;                   // variable base column, or -1
        public (double X, double Y, double Th) Frame { get; set; }   // leader's drawn pose (G0)
    }

    private readonly List<Body> _bodies = [];
    private readonly Dictionary<string, int> _bodyOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double X, double Y, double Th)> _offset = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _radius = new(StringComparer.Ordinal);

    private double _length = 1;   // characteristic length: the board diagonal
    private int _columns;
    private int _rows;

    private PcbConstraintSolver(
        PcbLayout layout, IReadOnlyList<PcbConstraint> constraints, PcbConstraintSolverSettings settings)
    {
        _layout = layout;
        _constraints = constraints;
        _settings = settings;
    }

    internal static PcbConstraintSolveResult Solve(
        PcbLayout layout, IReadOnlyList<PcbConstraint> constraints, PcbConstraintSolverSettings settings) =>
        new PcbConstraintSolver(layout, constraints, settings).Run();

    // =====================================================================
    //  Set-up: bodies, variables, seeds
    // =====================================================================

    private PcbConstraintSolveResult Run()
    {
        var diagnostics = new List<string>();
        _length = BoardDiagonal();
        BuildBodies();
        BuildRadii();

        _columns = _bodies.Count(b => !b.Grounded) * 3;
        _rows = _constraints.Sum(RowCount);

        var x = Seed();

        if (_rows == 0)
        {
            // No residuals: nothing to solve. Report the free DOF honestly (a lone Group, or
            // only Locks) and hand back the layout unchanged.
            Note(diagnostics);
            int free0 = _columns;
            if (free0 > 0)
                diagnostics.Add(FreedomNote(free0));
            return new PcbConstraintSolveResult(
                true, 0, 0, free0, 0, diagnostics, BuildSolved(x));
        }

        var residual = new double[_rows];
        var jacobian = new double[_rows * _columns];

        double worst = Evaluate(x, residual);
        int iteration = 0, steps = 0;
        double lambda = 1e-3;

        while (_columns > 0 && worst > _settings.Tolerance && iteration < _settings.MaxIterations)
        {
            iteration++;
            FillJacobian(x, jacobian);
            var normal = new double[_columns * _columns];
            var gradient = new double[_columns];
            NormalEquations(jacobian, residual, normal, gradient);

            double maxDiagonal = 0;
            for (int i = 0; i < _columns; i++)
                maxDiagonal = Math.Max(maxDiagonal, normal[i * _columns + i]);
            // Exact-zero semantic test: no first-order motion improves the residual.
            if (maxDiagonal <= 0)
                break;

            var before = (double[])x.Clone();
            bool accepted = false;
            for (int attempt = 0; attempt < 12 && !accepted; attempt++)
            {
                var damped = (double[])normal.Clone();
                for (int i = 0; i < _columns; i++)
                    damped[i * _columns + i] += lambda * maxDiagonal;

                if (!SolveSpd(damped, gradient, _columns, out var step))
                {
                    lambda *= 8;
                    continue;
                }

                for (int i = 0; i < _columns; i++)
                    x[i] = before[i] - step[i];   // the step solves A δ = Jᵀr, so descend by −δ
                double candidate = Evaluate(x, residual);
                if (candidate < worst)
                {
                    worst = candidate;
                    lambda = Math.Max(lambda / 3, 1e-12);
                    accepted = true;
                    steps++;
                }
                else
                {
                    lambda *= 8;
                }
            }

            if (!accepted)
            {
                Array.Copy(before, x, _columns);
                worst = Evaluate(x, residual);
                break;   // no damping value improves it
            }
        }

        bool converged = worst <= _settings.Tolerance;

        // Rank at the final configuration.
        FillJacobian(x, jacobian);
        var final = new double[_columns * _columns];
        var unusedGradient = new double[_columns];
        NormalEquations(jacobian, residual, final, unusedGradient);
        int rank = Rank(final, _columns, RankRelativeTolerance);

        Note(diagnostics);
        if (!converged)
        {
            WorstConstraints(x, residual, diagnostics);
            if (steps == 0 && _columns > 0)
                diagnostics.Add(
                    "no first-order motion improves the residual: the starting layout is a "
                    + "stationary configuration for these constraints (a Perpendicular constraint on "
                    + "already-parallel edges is the usual cause). Place the component roughly where it "
                    + "belongs and solve again.");
        }
        if (_columns - rank > 0)
            diagnostics.Add(FreedomNote(_columns - rank));

        // On failure the source layout is untouched (Solved stays null); on success a NEW layout
        // at the solved poses is produced, so the pads/copper/nets derive from the moved placements.
        return new PcbConstraintSolveResult(
            converged, iteration, worst, _columns, rank, diagnostics,
            converged ? BuildSolved(x) : null);
    }

    /// <summary>The board's bounding-box diagonal — the characteristic length that turns angular
    /// residuals into lengths and normalizes the rotation variable.</summary>
    private double BoardDiagonal()
    {
        var min = new Vector2d(double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vector2d(double.NegativeInfinity, double.NegativeInfinity);
        foreach (var p in _layout.Board.OutlinePoints)
        {
            min = new Vector2d(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y));
            max = new Vector2d(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y));
        }
        double diagonal = (max - min).Length;
        return diagonal > 0 ? diagonal : 1;   // exact-zero fallback: a degenerate outline
    }

    /// <summary>Unions placements into rigid bodies (singletons plus <c>Group</c>s), grounds a
    /// body whenever a member is <c>Lock</c>ed, and captures each member's offset from its body
    /// frame off the drawn layout.</summary>
    private void BuildBodies()
    {
        var drawn = DrawnPoses(_layout);
        int Index(string reference)
        {
            for (int i = 0; i < _layout.Placements.Count; i++)
                if (_layout.Placements[i].Reference == reference)
                    return i;
            return int.MaxValue;
        }

        // union-find over the participating placement refs
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string a)
        {
            parent.TryAdd(a, a);
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(string a, string b) { parent[Find(a)] = Find(b); }

        foreach (var constraint in _constraints)
            foreach (var reference in constraint.Placements)
                parent.TryAdd(reference, reference);
        foreach (var constraint in _constraints)
            if (constraint is GroupConstraint group)
                for (int i = 1; i < group.References.Count; i++)
                    Union(group.References[0], group.References[i]);

        // roots → bodies
        var membersByRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var reference in parent.Keys)
            (membersByRoot.TryGetValue(Find(reference), out var list)
                ? list : membersByRoot[Find(reference)] = []).Add(reference);

        var grounded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constraint in _constraints)
            if (constraint is LockConstraint locked)
                grounded.Add(Find(locked.Reference));

        // Bodies are created in leader-placement order so the variable layout — and therefore
        // the whole solve — is a pure function of the layout and the constraints (deterministic).
        foreach (var members in membersByRoot.Values)
            members.Sort((a, b) => Index(a).CompareTo(Index(b)));   // leader = first placed
        foreach (var members in membersByRoot.Values.OrderBy(m => Index(m[0])))
        {
            var leaderPose = drawn[members[0]];
            var body = new Body
            {
                Members = members,
                Grounded = grounded.Contains(Find(members[0])),
                Frame = leaderPose,
            };
            int bodyIndex = _bodies.Count;
            _bodies.Add(body);
            foreach (var reference in members)
            {
                _bodyOf[reference] = bodyIndex;
                _offset[reference] = OffsetOf(leaderPose, drawn[reference]);
            }
        }

        int column = 0;
        foreach (var body in _bodies)
            if (!body.Grounded)
            {
                body.Block = column;
                column += 3;
            }
    }

    /// <summary>The offset O such that <c>G0 ∘ O</c> reproduces the member's drawn pose:
    /// <c>O = G0⁻¹ ∘ member</c>.</summary>
    private static (double X, double Y, double Th) OffsetOf(
        (double X, double Y, double Th) frame, (double X, double Y, double Th) member)
    {
        double dx = member.X - frame.X, dy = member.Y - frame.Y;
        double c = Math.Cos(frame.Th), s = Math.Sin(frame.Th);
        return (c * dx + s * dy, -s * dx + c * dy, member.Th - frame.Th);
    }

    /// <summary>Each placement's footprint extent modelled as a bounding circle about its origin
    /// (rotation-invariant, conservative), enclosing every pad's copper.</summary>
    private void BuildRadii()
    {
        foreach (var placement in _layout.Placements)
        {
            double r = 0;
            var footprint = _layout.Schematic.Find(placement.Reference)?.Definition.Footprint;
            if (footprint is not null)
                foreach (var pad in footprint.Pads)
                {
                    double reach = pad.Center.Length + 0.5 * Math.Sqrt(pad.Width * pad.Width + pad.Height * pad.Height);
                    r = Math.Max(r, reach);
                }
            _radius[placement.Reference] = r;
        }
    }

    private double[] Seed()
    {
        var x = new double[_columns];
        foreach (var body in _bodies)
            if (!body.Grounded)
            {
                x[body.Block] = body.Frame.X;
                x[body.Block + 1] = body.Frame.Y;
                x[body.Block + 2] = body.Frame.Th * _length;   // scaled angle variable
            }
        return x;
    }

    // =====================================================================
    //  Reference evaluation (group-aware, with derivatives)
    // =====================================================================

    /// <summary>A reference point evaluated at the working poses: its world position, the free
    /// body it rides (or −1), and that body's world origin (the moment arm for the θ column).</summary>
    private readonly record struct EvalPoint(Vector2d World, int Block, Vector2d Origin);

    /// <summary>A reference direction evaluated at the working poses: its world (unit) direction
    /// and the free body it rides (or −1).</summary>
    private readonly record struct EvalDir(Vector2d World, int Block);

    private EvalPoint EvaluatePoint(double[] x, in PlacementPoint point)
    {
        if (point.Reference is null)
            return new EvalPoint(point.Local, -1, Vector2d.Zero);
        var body = _bodies[_bodyOf[point.Reference]];
        var offset = _offset[point.Reference];
        // q = O · local (the member's fixed offset)
        double co = Math.Cos(offset.Th), so = Math.Sin(offset.Th);
        var q = new Vector2d(
            co * point.Local.X - so * point.Local.Y + offset.X,
            so * point.Local.X + co * point.Local.Y + offset.Y);
        var (gx, gy, gth, block) = Pose(x, body);
        double cg = Math.Cos(gth), sg = Math.Sin(gth);
        var world = new Vector2d(cg * q.X - sg * q.Y + gx, sg * q.X + cg * q.Y + gy);
        return new EvalPoint(world, block, new Vector2d(gx, gy));
    }

    private EvalDir EvaluateDir(double[] x, in PlacementDirection direction)
    {
        if (direction.Reference is null)
            return new EvalDir(direction.Local, -1);
        var body = _bodies[_bodyOf[direction.Reference]];
        var offset = _offset[direction.Reference];
        double co = Math.Cos(offset.Th), so = Math.Sin(offset.Th);
        var q = new Vector2d(
            co * direction.Local.X - so * direction.Local.Y,
            so * direction.Local.X + co * direction.Local.Y);
        var (_, _, gth, block) = Pose(x, body);
        double cg = Math.Cos(gth), sg = Math.Sin(gth);
        return new EvalDir(new Vector2d(cg * q.X - sg * q.Y, sg * q.X + cg * q.Y), block);
    }

    /// <summary>The working pose of a body (from the variables when free, the fixed frame when
    /// grounded) plus its variable block.</summary>
    private (double X, double Y, double Th, int Block) Pose(double[] x, Body body)
    {
        if (body.Grounded)
            return (body.Frame.X, body.Frame.Y, body.Frame.Th, -1);
        int b = body.Block;
        return (x[b], x[b + 1], x[b + 2] / _length, b);
    }

    /// <summary>The member angle (group angle + member offset angle) of a reference.</summary>
    private (double Angle, int Block) MemberAngle(double[] x, string reference)
    {
        var body = _bodies[_bodyOf[reference]];
        var (_, _, gth, block) = Pose(x, body);
        return (gth + _offset[reference].Th, block);
    }

    // ---- Jacobian accumulation ----

    /// <summary>Accumulates a point reference's contribution to one residual row, given the
    /// scalar residual's sensitivity <paramref name="g"/> to the world point.</summary>
    private void AddPoint(double[] jacobian, int row, in EvalPoint p, in Vector2d g)
    {
        if (p.Block < 0)
            return;
        int b = p.Block;
        jacobian[row * _columns + b] += g.X;
        jacobian[row * _columns + b + 1] += g.Y;
        // θ column: the point rotates about the body origin with arm perp(world − origin); the
        // variable is scaled by the characteristic length, so the derivative divides by it.
        var arm = (p.World - p.Origin).Perpendicular;
        jacobian[row * _columns + b + 2] += (g.X * arm.X + g.Y * arm.Y) / _length;
    }

    /// <summary>Accumulates a direction reference's contribution to one residual row, given the
    /// scalar residual's sensitivity <paramref name="g"/> to the world direction.</summary>
    private void AddDir(double[] jacobian, int row, in EvalDir d, in Vector2d g)
    {
        if (d.Block < 0)
            return;
        var arm = d.World.Perpendicular;   // d(world dir)/dθ
        jacobian[row * _columns + d.Block + 2] += (g.X * arm.X + g.Y * arm.Y) / _length;
    }

    // =====================================================================
    //  Residuals and Jacobian
    // =====================================================================

    private double Evaluate(double[] x, double[] residual)
    {
        int row = 0;
        foreach (var constraint in _constraints)
            Residuals(x, constraint, residual, ref row);
        double worst = 0;
        for (int i = 0; i < residual.Length; i++)
            worst = Math.Max(worst, Math.Abs(residual[i]));
        return worst;
    }

    private void FillJacobian(double[] x, double[] jacobian)
    {
        Array.Clear(jacobian);
        int row = 0;
        foreach (var constraint in _constraints)
        {
            Jacobian(x, constraint, jacobian, row);
            row += RowCount(constraint);
        }
    }

    private static int RowCount(PcbConstraint constraint) => constraint switch
    {
        LockConstraint or GroupConstraint => 0,
        CoincidentConstraint or AlignEdgeConstraint => 2,
        _ => 1,
    };

    private void Residuals(double[] x, PcbConstraint constraint, double[] residual, ref int row)
    {
        switch (constraint)
        {
            case LockConstraint or GroupConstraint:
                break;

            case FixRotationConstraint fix:
                residual[row++] = Wrap(MemberAngle(x, fix.Reference).Angle - DrawnAngle(fix.Reference)) * _length;
                break;

            case OrientConstraint orient:
                residual[row++] = Wrap(MemberAngle(x, orient.Reference).Angle - Radians(orient.Degrees)) * _length;
                break;

            case CoincidentConstraint c:
            {
                var w = EvaluatePoint(x, c.A).World - EvaluatePoint(x, c.B).World;
                residual[row++] = w.X;
                residual[row++] = w.Y;
                break;
            }

            case DistanceConstraint d:
            {
                var w = EvaluatePoint(x, d.A).World - EvaluatePoint(x, d.B).World;
                residual[row++] = w.Length - d.Gap;
                break;
            }

            case AlignConstraint a:
            {
                var pa = EvaluatePoint(x, a.A).World;
                var pb = EvaluatePoint(x, a.B).World;
                residual[row++] = a.ShareX ? pa.X - pb.X : pa.Y - pb.Y;
                break;
            }

            case DirectionPairConstraint dir:
            {
                var a = EvaluateDir(x, dir.A).World;
                var b = EvaluateDir(x, dir.B).World;
                residual[row++] = (dir.Parallel ? a.Cross(b) : a.Dot(b)) * _length;
                break;
            }

            case PointOnLineConstraint pol:
                residual[row++] = OnLineResidual(x, pol.Point, pol.LinePoint, pol.LineDirection, pol.Offset);
                break;

            case AlignEdgeConstraint edge:
            {
                var a = EvaluateDir(x, edge.Component.Direction).World;
                var b = EvaluateDir(x, edge.Target.Direction).World;
                residual[row++] = a.Cross(b) * _length;
                residual[row++] = OnLineResidual(
                    x, edge.Component.Point, edge.Target.Point, edge.Target.Direction, edge.Side * edge.Gap);
                break;
            }

            case InsideRegionConstraint inside:
            {
                var center = EvaluatePoint(x, PlacementPoint.Origin(inside.Reference));
                var (d, _) = PolygonSignedDistance(inside.Polygon, center.World);
                double feasible = -(_radius[inside.Reference] + inside.Margin) - d;
                residual[row++] = Math.Min(feasible, 0);
                break;
            }

            case ClearOfConstraint clear:
            {
                var wa = EvaluatePoint(x, PlacementPoint.Origin(clear.A)).World;
                var wb = EvaluatePoint(x, PlacementPoint.Origin(clear.B)).World;
                double feasible = (wa - wb).Length - (_radius[clear.A] + _radius[clear.B] + clear.Distance);
                residual[row++] = Math.Min(feasible, 0);
                break;
            }

            case ClearOfRegionConstraint region:
            {
                var center = EvaluatePoint(x, PlacementPoint.Origin(region.Reference));
                var (d, _) = PolygonSignedDistance(region.Polygon, center.World);
                double feasible = d - (_radius[region.Reference] + region.Distance);
                residual[row++] = Math.Min(feasible, 0);
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled constraint kind {constraint.GetType().Name}.");
        }
    }

    private void Jacobian(double[] x, PcbConstraint constraint, double[] jacobian, int row)
    {
        switch (constraint)
        {
            case LockConstraint or GroupConstraint:
                break;

            case FixRotationConstraint fix:
                AddAngle(jacobian, row, MemberAngle(x, fix.Reference).Block);
                break;

            case OrientConstraint orient:
                AddAngle(jacobian, row, MemberAngle(x, orient.Reference).Block);
                break;

            case CoincidentConstraint c:
            {
                var ea = EvaluatePoint(x, c.A);
                var eb = EvaluatePoint(x, c.B);
                AddPoint(jacobian, row, ea, Vector2d.UnitX);
                AddPoint(jacobian, row, eb, -Vector2d.UnitX);
                AddPoint(jacobian, row + 1, ea, Vector2d.UnitY);
                AddPoint(jacobian, row + 1, eb, -Vector2d.UnitY);
                break;
            }

            case DistanceConstraint d:
            {
                var ea = EvaluatePoint(x, d.A);
                var eb = EvaluatePoint(x, d.B);
                var w = ea.World - eb.World;
                double len = w.Length;
                var unit = len > 0 ? w / len : Vector2d.UnitX;   // |w| not differentiable at 0
                AddPoint(jacobian, row, ea, unit);
                AddPoint(jacobian, row, eb, -unit);
                break;
            }

            case AlignConstraint a:
            {
                var g = a.ShareX ? Vector2d.UnitX : Vector2d.UnitY;
                AddPoint(jacobian, row, EvaluatePoint(x, a.A), g);
                AddPoint(jacobian, row, EvaluatePoint(x, a.B), -g);
                break;
            }

            case DirectionPairConstraint dir:
                DirectionJacobian(x, dir.A, dir.B, dir.Parallel, jacobian, row);
                break;

            case PointOnLineConstraint pol:
                OnLineJacobian(x, pol.Point, pol.LinePoint, pol.LineDirection, jacobian, row);
                break;

            case AlignEdgeConstraint edge:
                DirectionJacobian(x, edge.Component.Direction, edge.Target.Direction, true, jacobian, row);
                OnLineJacobian(x, edge.Component.Point, edge.Target.Point, edge.Target.Direction, jacobian, row + 1);
                break;

            case InsideRegionConstraint inside:
            {
                var center = EvaluatePoint(x, PlacementPoint.Origin(inside.Reference));
                var (d, grad) = PolygonSignedDistance(inside.Polygon, center.World);
                if (-(_radius[inside.Reference] + inside.Margin) - d < 0)   // active
                    AddPoint(jacobian, row, center, -grad);
                break;
            }

            case ClearOfConstraint clear:
            {
                var ea = EvaluatePoint(x, PlacementPoint.Origin(clear.A));
                var eb = EvaluatePoint(x, PlacementPoint.Origin(clear.B));
                var w = ea.World - eb.World;
                double len = w.Length;
                if (len - (_radius[clear.A] + _radius[clear.B] + clear.Distance) < 0)   // active
                {
                    var unit = len > 0 ? w / len : Vector2d.UnitX;
                    AddPoint(jacobian, row, ea, unit);
                    AddPoint(jacobian, row, eb, -unit);
                }
                break;
            }

            case ClearOfRegionConstraint region:
            {
                var center = EvaluatePoint(x, PlacementPoint.Origin(region.Reference));
                var (d, grad) = PolygonSignedDistance(region.Polygon, center.World);
                if (d - (_radius[region.Reference] + region.Distance) < 0)   // active
                    AddPoint(jacobian, row, center, grad);
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled constraint kind {constraint.GetType().Name}.");
        }
    }

    /// <summary>A pure-rotation residual column: the angle is scaled by the length and the
    /// variable divided by it, so the two cancel to a unit column.</summary>
    private void AddAngle(double[] jacobian, int row, int block)
    {
        if (block >= 0)
            jacobian[row * _columns + block + 2] += 1;   // L · (dWrap/dθ) / L
    }

    private double OnLineResidual(
        double[] x, in PlacementPoint point, in PlacementPoint linePoint, in PlacementDirection lineDir, double offset)
    {
        var d = EvaluateDir(x, lineDir).World;
        var w = EvaluatePoint(x, point).World - EvaluatePoint(x, linePoint).World;
        return d.Cross(w) - offset;   // signed perpendicular distance (d is unit)
    }

    private void OnLineJacobian(
        double[] x, in PlacementPoint point, in PlacementPoint linePoint, in PlacementDirection lineDir,
        double[] jacobian, int row)
    {
        var ed = EvaluateDir(x, lineDir);
        var ep = EvaluatePoint(x, point);
        var eq = EvaluatePoint(x, linePoint);
        var d = ed.World;
        var w = ep.World - eq.World;
        // r = cross(d, w) = d.x·w.y − d.y·w.x.  ∂/∂w = (−d.y, d.x); ∂/∂d = (w.y, −w.x).
        var gPoint = new Vector2d(-d.Y, d.X);
        AddPoint(jacobian, row, ep, gPoint);
        AddPoint(jacobian, row, eq, -gPoint);
        AddDir(jacobian, row, ed, new Vector2d(w.Y, -w.X));
    }

    private void DirectionJacobian(
        double[] x, in PlacementDirection a, in PlacementDirection b, bool parallel, double[] jacobian, int row)
    {
        var ea = EvaluateDir(x, a);
        var eb = EvaluateDir(x, b);
        var da = ea.World;
        var db = eb.World;
        if (parallel)
        {
            // r = cross(a, b)·L.  ∂/∂a = (b.y, −b.x)·L; ∂/∂b = (−a.y, a.x)·L.
            AddDir(jacobian, row, ea, new Vector2d(db.Y, -db.X) * _length);
            AddDir(jacobian, row, eb, new Vector2d(-da.Y, da.X) * _length);
        }
        else
        {
            // r = dot(a, b)·L.  ∂/∂a = b·L; ∂/∂b = a·L.
            AddDir(jacobian, row, ea, db * _length);
            AddDir(jacobian, row, eb, da * _length);
        }
    }

    // =====================================================================
    //  Geometry helpers
    // =====================================================================

    /// <summary>Signed distance from <paramref name="p"/> to a polygon's boundary (negative
    /// inside), plus the gradient of that signed distance in <paramref name="p"/> (a unit vector).
    /// Works for any simple polygon: the nearest boundary point plus an even-odd inside test.</summary>
    private static (double Signed, Vector2d Gradient) PolygonSignedDistance(
        IReadOnlyList<Vector2d> polygon, Vector2d p)
    {
        double bestSq = double.PositiveInfinity;
        var nearest = p;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var closest = ClosestOnSegment(polygon[j], polygon[i], p);
            double dsq = (p - closest).LengthSquared;
            if (dsq < bestSq)
            {
                bestSq = dsq;
                nearest = closest;
            }
        }
        double distance = Math.Sqrt(bestSq);
        bool inside = PcbGeometry.PolygonContains(polygon, p);
        double signed = inside ? -distance : distance;
        if (!(distance > 0))   // exact-zero guard: p on the boundary has no gradient direction
            return (signed, Vector2d.UnitX);
        var outward = (p - nearest) / distance;   // ∇(distance-to-boundary)
        return (signed, inside ? -outward : outward);
    }

    private static Vector2d ClosestOnSegment(Vector2d a, Vector2d b, Vector2d p)
    {
        var ab = b - a;
        double lengthSq = ab.LengthSquared;
        if (!(lengthSq > 0))   // exact-zero guard: a degenerate edge is its own point
            return a;
        double t = Math.Clamp((p - a).Dot(ab) / lengthSq, 0, 1);
        return a + ab * t;
    }

    private double DrawnAngle(string reference)
    {
        foreach (var placement in _layout.Placements)
            if (placement.Reference == reference)
                return Radians(placement.RotationDegrees);
        return 0;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Wraps an angle difference into (−π, π] — the wrap-free residual (its derivative in
    /// the angle is 1 almost everywhere), so a rotation residual never jumps 2π at the seam.</summary>
    private static double Wrap(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    // =====================================================================
    //  Reporting and the solved layout
    // =====================================================================

    private void Note(List<string> diagnostics)
    {
        var mentioned = _bodyOf.Keys.ToHashSet(StringComparer.Ordinal);
        var unmentioned = _layout.Placements
            .Select(p => p.Reference)
            .Where(r => !mentioned.Contains(r))
            .ToList();
        if (unmentioned.Count > 0)
        {
            string names = string.Join(", ", unmentioned.Take(6));
            if (unmentioned.Count > 6)
                names += $", +{unmentioned.Count - 6} more";
            diagnostics.Add($"no constraint mentions {names} — left where they were placed");
        }
    }

    private static string FreedomNote(int remaining) =>
        $"{remaining} degree{(remaining == 1 ? "" : "s")} of freedom remain: the constraints do not "
        + "pin the layout completely (add a constraint, or lock a placement)";

    private void WorstConstraints(double[] x, double[] residual, List<string> diagnostics)
    {
        Evaluate(x, residual);
        var offenders = new List<(string Name, double Residual)>();
        int row = 0;
        foreach (var constraint in _constraints)
        {
            int rows = RowCount(constraint);
            double peak = 0;
            for (int i = 0; i < rows; i++)
                peak = Math.Max(peak, Math.Abs(residual[row + i]));
            row += rows;
            if (rows > 0)
                offenders.Add((constraint.Name, peak));
        }
        foreach (var (name, peak) in offenders.OrderByDescending(o => o.Residual).Take(3))
            diagnostics.Add($"'{name}' is off by {peak:g4}");
        diagnostics.Add("nothing was moved — the layout is unchanged");
    }

    /// <summary>A NEW layout at the solved poses (the source is never mutated). Placement order is
    /// preserved, so the solved layout's own save is a fixed point.</summary>
    private PcbLayout BuildSolved(double[] x)
    {
        var solved = new PcbLayout(_layout.Schematic, _layout.Board, _layout.BoardFrame);
        foreach (var placement in _layout.Placements)
        {
            var pose = SolvedPose(x, placement.Reference);
            solved.Place(placement.Reference, pose.X, pose.Y, pose.Th * 180.0 / Math.PI, placement.Side);
        }
        return solved;
    }

    /// <summary>A placement's solved pose: <c>G ∘ offset</c> for a member of a free body, its
    /// drawn pose otherwise (grounded, or mentioned by no constraint).</summary>
    private (double X, double Y, double Th) SolvedPose(double[] x, string reference)
    {
        if (!_bodyOf.TryGetValue(reference, out int bodyIndex))
            return DrawnPoses(_layout)[reference];
        var body = _bodies[bodyIndex];
        var (gx, gy, gth, _) = Pose(x, body);
        var (ox, oy, oth) = _offset[reference];
        double c = Math.Cos(gth), s = Math.Sin(gth);
        return (gx + c * ox - s * oy, gy + s * ox + c * oy, gth + oth);
    }

    // =====================================================================
    //  Static drawn-pose helpers (used by the builder's branch-selector capture)
    // =====================================================================

    internal static Dictionary<string, (double X, double Y, double Th)> DrawnPoses(PcbLayout layout)
    {
        var poses = new Dictionary<string, (double X, double Y, double Th)>(StringComparer.Ordinal);
        foreach (var placement in layout.Placements)
            poses[placement.Reference] = (placement.X, placement.Y, Radians(placement.RotationDegrees));
        return poses;
    }

    internal static Vector2d WorldPoint(
        Dictionary<string, (double X, double Y, double Th)> poses, in PlacementPoint point)
    {
        if (point.Reference is null)
            return point.Local;
        var (x, y, th) = poses[point.Reference];
        double c = Math.Cos(th), s = Math.Sin(th);
        return new Vector2d(x + c * point.Local.X - s * point.Local.Y, y + s * point.Local.X + c * point.Local.Y);
    }

    internal static Vector2d WorldDirection(
        Dictionary<string, (double X, double Y, double Th)> poses, in PlacementDirection direction)
    {
        if (direction.Reference is null)
            return direction.Local;
        double th = poses[direction.Reference].Th;
        double c = Math.Cos(th), s = Math.Sin(th);
        return new Vector2d(c * direction.Local.X - s * direction.Local.Y,
            s * direction.Local.X + c * direction.Local.Y);
    }

    // =====================================================================
    //  Dense linear algebra (small, dense, symmetric positive definite)
    // =====================================================================

    private void NormalEquations(double[] jacobian, double[] residual, double[] normal, double[] gradient)
    {
        Array.Clear(normal);
        Array.Clear(gradient);
        for (int r = 0; r < _rows; r++)
        {
            int offset = r * _columns;
            double rr = residual[r];
            for (int i = 0; i < _columns; i++)
            {
                double ji = jacobian[offset + i];
                if (ji == 0)   // exact-zero skip: the Jacobian is sparse (each constraint touches ≤ 9 columns)
                    continue;
                gradient[i] += ji * rr;
                int rowBase = i * _columns;
                for (int j = 0; j < _columns; j++)
                    normal[rowBase + j] += ji * jacobian[offset + j];
            }
        }
    }

    private static bool SolveSpd(double[] a, double[] b, int n, out double[] x)
    {
        var l = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i * n + j];
                for (int k = 0; k < j; k++)
                    sum -= l[i * n + k] * l[j * n + k];
                if (i == j)
                {
                    if (sum <= 0)   // exact-zero/negative pivot: not positive definite
                    {
                        x = [];
                        return false;
                    }
                    l[i * n + i] = Math.Sqrt(sum);
                }
                else
                {
                    l[i * n + j] = sum / l[j * n + j];
                }
            }
        }

        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = b[i];
            for (int k = 0; k < i; k++)
                sum -= l[i * n + k] * y[k];
            y[i] = sum / l[i * n + i];
        }
        x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = y[i];
            for (int k = i + 1; k < n; k++)
                sum -= l[k * n + i] * x[k];
            x[i] = sum / l[i * n + i];
        }
        return true;
    }

    /// <summary>Rank of a symmetric PSD matrix by diagonally pivoted Cholesky — the standard
    /// rank-revealing factorization, so redundant constraint rows do not inflate the DOF count.</summary>
    private static int Rank(double[] a, int n, double relativeTolerance)
    {
        if (n == 0)
            return 0;
        var m = (double[])a.Clone();
        var live = new bool[n];
        Array.Fill(live, true);

        double first = 0;
        for (int i = 0; i < n; i++)
            first = Math.Max(first, m[i * n + i]);
        if (first <= 0)          // exact-zero semantic test: nothing is constrained
            return 0;
        double floor = first * relativeTolerance * relativeTolerance;   // pivots are squared singular values

        int rank = 0;
        for (int step = 0; step < n; step++)
        {
            int pivot = -1;
            double best = floor;
            for (int i = 0; i < n; i++)
                if (live[i] && m[i * n + i] > best)
                {
                    best = m[i * n + i];
                    pivot = i;
                }
            if (pivot < 0)
                break;

            rank++;
            live[pivot] = false;
            double d = m[pivot * n + pivot];
            for (int i = 0; i < n; i++)
            {
                if (!live[i])
                    continue;
                double factor = m[i * n + pivot] / d;
                if (factor == 0)
                    continue;
                for (int j = 0; j < n; j++)
                    if (live[j])
                        m[i * n + j] -= factor * m[pivot * n + j];
            }
        }
        return rank;
    }
}
