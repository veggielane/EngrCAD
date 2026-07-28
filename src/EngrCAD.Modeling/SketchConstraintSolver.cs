using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// The variable mapping behind <see cref="ConstrainedSketch"/>: one flat vector of
/// unknowns over the sketch's normalized loops (outer loop first, then holes).
///
/// <para><b>The mapping.</b> Endpoints shared between consecutive segments are ONE point
/// variable — joint j of a loop is the start of segment j (= the end of segment j−1), so
/// a chain of n segments carries n joints and the loop closes by construction, healing
/// any sub-weld drawing gap in the process. Arcs additionally carry their center (2) and
/// radius (1) as variables, tied to their endpoint joints by two internal
/// endpoint-consistency rows (|joint − center| = radius), which is what keeps the
/// variables consistent with the arc's exact geometry: an arc through two pinned joints
/// keeps exactly one net degree of freedom, its bulge. A loop that is a single full
/// circle has no joints — just center + radius (a phase variable would be spurious
/// freedom). Bézier control points are deliberately NOT variables: constraints cannot
/// reach them in v1, and on rebuild they follow the similarity that maps the segment's
/// old chord onto its new one, keeping the curve's shape relative to its endpoints.</para>
/// </summary>
internal sealed class SketchVariables
{
    internal sealed class Loop
    {
        public required IReadOnlyList<SketchSegment> Segments { get; init; }

        /// <summary>Variable index of joint j's x (y at +1); empty for a single-circle loop.</summary>
        public required int[] JointVars { get; init; }

        /// <summary>Variable index of segment s's arc center x (y at +1); −1 for non-arcs.</summary>
        public required int[] CenterVars { get; init; }

        /// <summary>Variable index of segment s's arc radius; −1 for non-arcs.</summary>
        public required int[] RadiusVars { get; init; }

        public required bool SingleCircle { get; init; }

        public int JointCount => JointVars.Length;
    }

    public required Sketch Source { get; init; }
    public required IReadOnlyList<Loop> Loops { get; init; }
    public required double[] Seed { get; init; }

    /// <summary>The sketch's own scale (largest bounds extent at the seed), used to
    /// scale dimensionless angular residuals into lengths — the MateSolver rule that
    /// makes one linear tolerance meaningful for every residual.</summary>
    public required double CharacteristicLength { get; init; }

    public int Count => Seed.Length;

    public static SketchVariables Build(Sketch sketch)
    {
        var seed = new List<double>();
        var loops = new List<Loop>();
        var bounds = Aabb.Empty;
        foreach (var loopSketch in EnumerateLoops(sketch))
        {
            var segments = loopSketch.Segments;
            bounds = bounds.Union(loopSketch.Bounds);
            bool singleCircle = segments.Count == 1
                && segments[0] is ArcSeg only && only.IsFullCircle;

            int[] jointVars = new int[singleCircle ? 0 : segments.Count];
            for (int j = 0; j < jointVars.Length; j++)
            {
                jointVars[j] = seed.Count;
                var p = segments[j].Start;
                seed.Add(p.X);
                seed.Add(p.Y);
            }

            int[] centerVars = new int[segments.Count];
            int[] radiusVars = new int[segments.Count];
            Array.Fill(centerVars, -1);
            Array.Fill(radiusVars, -1);
            for (int s = 0; s < segments.Count; s++)
            {
                if (segments[s] is not ArcSeg arc)
                    continue;
                centerVars[s] = seed.Count;
                seed.Add(arc.Center.X);
                seed.Add(arc.Center.Y);
                radiusVars[s] = seed.Count;
                seed.Add(arc.Radius);
            }

            loops.Add(new Loop
            {
                Segments = segments,
                JointVars = jointVars,
                CenterVars = centerVars,
                RadiusVars = radiusVars,
                SingleCircle = singleCircle,
            });
        }

        double extent = Math.Max(bounds.Size.X, bounds.Size.Y);
        return new SketchVariables
        {
            Source = sketch,
            Loops = loops,
            Seed = [.. seed],
            // Exact-zero semantic test: a sketch whose bounds are a point exposes no
            // scale, so 1 is the only honest fallback (the constructor has already
            // rejected such sketches as enclosing no area, so this is belt-and-braces).
            CharacteristicLength = extent > 0 ? extent : 1,
        };
    }

    private static IEnumerable<Sketch> EnumerateLoops(Sketch sketch)
    {
        yield return sketch;
        foreach (var hole in sketch.Holes)
            yield return hole;
    }

    internal static Vector2d Point(double[] x, int variable) => new(x[variable], x[variable + 1]);

    /// <summary>
    /// The solved configuration as an ordinary <see cref="Sketch"/>. The JOINTS are
    /// authoritative: lines take them verbatim, arcs re-derive angles from them (so a
    /// chain's shared corners are bit-identical and closure never depends on solver
    /// tolerance), and bézier control points follow their chord's similarity map. Runs
    /// the ordinary <see cref="Sketch"/> constructor, so closure, area and winding
    /// validation happen in exactly one place.
    /// </summary>
    public Sketch Rebuild(double[] x)
    {
        var rebuilt = new List<List<SketchSegment>>(Loops.Count);
        foreach (var loop in Loops)
            rebuilt.Add(RebuildLoop(loop, x));
        var holes = new List<Sketch>(rebuilt.Count - 1);
        for (int h = 1; h < rebuilt.Count; h++)
            holes.Add(new Sketch(rebuilt[h], []));
        return new Sketch(rebuilt[0], holes);
    }

    private static List<SketchSegment> RebuildLoop(Loop loop, double[] x)
    {
        var result = new List<SketchSegment>(loop.Segments.Count);
        for (int s = 0; s < loop.Segments.Count; s++)
        {
            var segment = loop.Segments[s];
            if (loop.SingleCircle)
            {
                // A full-circle loop has no joints; center/radius are direct variables
                // and the drawn phase (start angle, sweep sign) is carried verbatim.
                var circle = (ArcSeg)segment;
                result.Add(new ArcSeg(
                    Point(x, loop.CenterVars[s]), x[loop.RadiusVars[s]],
                    circle.StartAngle, circle.Sweep));
                continue;
            }

            var start = Point(x, loop.JointVars[s]);
            var end = Point(x, loop.JointVars[(s + 1) % loop.JointCount]);
            switch (segment)
            {
                case LineSeg:
                    result.Add(new LineSeg(start, end));
                    break;

                case ArcSeg arc:
                {
                    var center = Point(x, loop.CenterVars[s]);
                    // The mean of the two endpoint distances: a converged solve has
                    // already forced both within the solver tolerance of the radius
                    // variable, and the mean keeps both ends symmetric. Taking the
                    // radius from the joints (rather than the radius variable) is what
                    // guarantees ArcSeg.Start/End land back on the joints to rounding,
                    // far inside the 1e-9 closure weld.
                    double radius = 0.5 * ((start - center).Length + (end - center).Length);
                    double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
                    double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
                    double sweep = a1 - a0;
                    // The drawn sweep's SIGN is the branch selector (the same convention
                    // as SketchBuilder.ArcTo): wrap the solved sweep into (0, 2π] with
                    // that sign. A drawn full circle mid-chain has raw sweep ~0 and maps
                    // back to ±2π. Angles are dimensionless, so 1e-12 rad is one of the
                    // legitimately absolute angular guards.
                    if (arc.Sweep > 0)
                        sweep = sweep > 1e-12 ? sweep : sweep + 2 * Math.PI;
                    else
                        sweep = sweep < -1e-12 ? sweep : sweep - 2 * Math.PI;
                    result.Add(new ArcSeg(center, radius, a0, sweep));
                    break;
                }

                case CubicSeg cubic:
                    result.Add(RebuildCubic(cubic, start, end));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown sketch segment kind {segment.GetType().Name}.");
            }
        }
        return result;
    }

    /// <summary>Control points ride the similarity (rotation + uniform scale) that maps
    /// the old chord onto the new one, so the bézier keeps its shape relative to its
    /// endpoints; the endpoints themselves are set to the joints EXACTLY, so closure
    /// never depends on this arithmetic.</summary>
    private static CubicSeg RebuildCubic(CubicSeg cubic, Vector2d start, Vector2d end)
    {
        var oldChord = cubic.P3 - cubic.P0;
        var newChord = end - start;
        if (!(oldChord.LengthSquared > 0))   // exact-zero division guard: chord-degenerate bézier translates
        {
            var delta = start - cubic.P0;
            return new CubicSeg(start, cubic.Control1 + delta, cubic.Control2 + delta, end);
        }
        var m = ComplexDivide(newChord, oldChord);
        return new CubicSeg(
            start,
            start + ComplexMultiply(m, cubic.Control1 - cubic.P0),
            start + ComplexMultiply(m, cubic.Control2 - cubic.P0),
            end);
    }

    private static Vector2d ComplexMultiply(in Vector2d a, in Vector2d b) =>
        new(a.X * b.X - a.Y * b.Y, a.X * b.Y + a.Y * b.X);

    private static Vector2d ComplexDivide(in Vector2d a, in Vector2d b) =>
        ComplexMultiply(a, new Vector2d(b.X, -b.Y)) / b.LengthSquared;
}

/// <summary>
/// One residual block of the sketch constraint system. Every residual is a LENGTH in
/// sketch units: point/distance/radius rows are lengths natively, and angular rows
/// (parallel, perpendicular, angle, expressed on unit directions) are multiplied by the
/// sketch's characteristic length — the MateSolver doctrine, so one linear tolerance
/// covers the whole system. Jacobians are ANALYTIC without exception (finite differences
/// cap accuracy near 1e-8, an order worse than the 1e-9 weld tier this aims at).
/// </summary>
internal abstract class SketchConstraint
{
    public required string Name { get; init; }

    public abstract int Rows { get; }

    public abstract void Residual(double[] x, double scale, double[] residual, int row);

    public abstract void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns);

    protected static Vector2d Point(double[] x, int variable) => SketchVariables.Point(x, variable);

    /// <summary>Jacobian writes ACCUMULATE: two ends of one constraint may share a
    /// variable (adjacent segments share joints), and their contributions must add.</summary>
    protected static void Add(double[] jacobian, int row, int columns, int variable, double value) =>
        jacobian[row * columns + variable] += value;

    /// <summary>Unit direction of the line p1→p2. Exact-zero guard: a degenerate
    /// (zero-length) line reports UnitX with zero derivatives — no NaN can enter the
    /// system; LM simply finds no step through it.</summary>
    protected static (Vector2d Hat, double Length) UnitDirection(double[] x, int p1, int p2)
    {
        var d = Point(x, p2) - Point(x, p1);
        double length = d.Length;
        return length > 0 ? (d / length, length) : (Vector2d.UnitX, 0);
    }

    /// <summary>∂(unit direction)/∂{p1.x, p1.y, p2.x, p2.y} — the (I − d̂d̂ᵀ)/|d|
    /// projection columns, exact.</summary>
    protected static void UnitDerivatives(in Vector2d hat, double length, Span<Vector2d> dHat)
    {
        if (!(length > 0))   // exact-zero guard: degenerate line has no direction gradient
        {
            dHat.Clear();
            return;
        }
        var dx = new Vector2d(1 - hat.X * hat.X, -hat.X * hat.Y) / length;
        var dy = new Vector2d(-hat.X * hat.Y, 1 - hat.Y * hat.Y) / length;
        dHat[0] = -dx;
        dHat[1] = -dy;
        dHat[2] = dx;
        dHat[3] = dy;
    }

    /// <summary>Unit vector from b towards a. |a − b| is not differentiable at 0; any
    /// unit direction is a valid descent direction there and one step separates the
    /// points (the MateSolver Distance convention).</summary>
    protected static Vector2d UnitBetween(in Vector2d a, in Vector2d b)
    {
        var u = a - b;
        double length = u.Length;
        return length > 0 ? u / length : Vector2d.UnitX;
    }
}

/// <summary>Two points coincide (2 rows). Also serves Concentric — two arc centers are
/// just two point variables.</summary>
internal sealed class CoincidentConstraint(int a, int b) : SketchConstraint
{
    public override int Rows => 2;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        residual[row] = x[a] - x[b];
        residual[row + 1] = x[a + 1] - x[b + 1];
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        Add(jacobian, row, columns, a, 1);
        Add(jacobian, row, columns, b, -1);
        Add(jacobian, row + 1, columns, a + 1, 1);
        Add(jacobian, row + 1, columns, b + 1, -1);
    }
}

/// <summary>Horizontal (equal y) or vertical (equal x) point pair — 1 row. A line's
/// horizontal/vertical constraint is this on its two endpoint joints.</summary>
internal sealed class AlignedConstraint(int a, int b, bool horizontal) : SketchConstraint
{
    private readonly int _offset = horizontal ? 1 : 0;   // horizontal pins the y difference

    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = x[b + _offset] - x[a + _offset];

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        Add(jacobian, row, columns, b + _offset, 1);
        Add(jacobian, row, columns, a + _offset, -1);
    }
}

/// <summary>
/// Relative direction of two lines — 1 row on unit directions, scaled by the
/// characteristic length. Parallel uses the CROSS form (linear in the angle error near
/// the solution); Perpendicular and Angle use the DOT form (dot − cos θ). An Angle of 0
/// or π is refused at the API layer in favour of Parallel, because d(cos)/dθ vanishes
/// exactly at the solution there and the rank report would count the row as inactive.
/// </summary>
internal sealed class DirectionConstraint(
    int a1, int a2, int b1, int b2, bool parallel, double cosine) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var (hatA, _) = UnitDirection(x, a1, a2);
        var (hatB, _) = UnitDirection(x, b1, b2);
        residual[row] = parallel
            ? hatA.Cross(hatB) * scale
            : (hatA.Dot(hatB) - cosine) * scale;
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (hatA, lengthA) = UnitDirection(x, a1, a2);
        var (hatB, lengthB) = UnitDirection(x, b1, b2);
        Span<Vector2d> dA = stackalloc Vector2d[4];
        Span<Vector2d> dB = stackalloc Vector2d[4];
        UnitDerivatives(hatA, lengthA, dA);
        UnitDerivatives(hatB, lengthB, dB);
        Span<int> varsA = [a1, a1 + 1, a2, a2 + 1];
        Span<int> varsB = [b1, b1 + 1, b2, b2 + 1];
        for (int k = 0; k < 4; k++)
        {
            Add(jacobian, row, columns, varsA[k],
                (parallel ? dA[k].Cross(hatB) : dA[k].Dot(hatB)) * scale);
            Add(jacobian, row, columns, varsB[k],
                (parallel ? hatA.Cross(dB[k]) : hatA.Dot(dB[k])) * scale);
        }
    }
}

/// <summary>
/// Line tangent to an arc that shares an endpoint joint with it — the sketcher's
/// ordinary line→fillet tangency. The residual is the radius direction's projection
/// onto the line, d̂·(c − J): together with the arc's endpoint-consistency row
/// (|J − c| = r) this is EXACT tangency, and it is FIRST order in every motion.
///
/// <para><b>Why not center-to-carrier distance = r?</b> That residual is also exact at
/// the solution, but sliding the shared joint along the line changes it only
/// QUADRATICALLY (√(r² + δ²) − r ≈ δ²/2r through the consistency row), so a solve
/// converged to 1e-9 leaves the tangency foot √(2r·1e-9) ≈ 1e-4 adrift — measured as
/// 3.6e-4 of area error on a fully-constrained rounded rectangle — and the near-zero
/// singular value corrupts the DOF rank. The perpendicularity form has no such
/// direction, and needs no branch selector (the seed keeps the center's side: LM
/// cannot cross c onto the line without the consistency residual objecting).</para>
/// </summary>
internal sealed class TangentAtJointConstraint(int p1, int p2, int center, int joint) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var (hat, _) = UnitDirection(x, p1, p2);
        residual[row] = hat.Dot(Point(x, center) - Point(x, joint));
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (hat, length) = UnitDirection(x, p1, p2);
        var w = Point(x, center) - Point(x, joint);
        Span<Vector2d> dHat = stackalloc Vector2d[4];
        UnitDerivatives(hat, length, dHat);
        Span<int> vars = [p1, p1 + 1, p2, p2 + 1];
        for (int k = 0; k < 4; k++)
            Add(jacobian, row, columns, vars[k], dHat[k].Dot(w));
        Add(jacobian, row, columns, center, hat.X);
        Add(jacobian, row, columns, center + 1, hat.Y);
        Add(jacobian, row, columns, joint, -hat.X);
        Add(jacobian, row, columns, joint + 1, -hat.Y);
    }
}

/// <summary>Line tangent to a FREE-STANDING arc (no shared joint — a hole circle
/// against an outer line): the arc's center sits exactly one radius from the line's
/// carrier, on the side it was DRAWN on (<paramref name="side"/> is the branch selector
/// captured from the seed) — 1 row, s·cross(d̂, c − p1) − r. Only legitimate without a
/// shared joint: see <see cref="TangentAtJointConstraint"/> for why the adjacent case
/// must not use this form.</summary>
internal sealed class TangentLineArcConstraint(
    int p1, int p2, int center, int radius, double side) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var (hat, _) = UnitDirection(x, p1, p2);
        var w = Point(x, center) - Point(x, p1);
        residual[row] = side * hat.Cross(w) - x[radius];
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (hat, length) = UnitDirection(x, p1, p2);
        var w = Point(x, center) - Point(x, p1);
        Span<Vector2d> dHat = stackalloc Vector2d[4];
        UnitDerivatives(hat, length, dHat);
        Span<int> vars = [p1, p1 + 1, p2, p2 + 1];
        for (int k = 0; k < 4; k++)
            Add(jacobian, row, columns, vars[k], side * dHat[k].Cross(w));
        // ∂w/∂p1 = −I and ∂w/∂c = +I, through cross(hat, ·).
        Add(jacobian, row, columns, p1, side * hat.Y);
        Add(jacobian, row, columns, p1 + 1, -side * hat.X);
        Add(jacobian, row, columns, center, -side * hat.Y);
        Add(jacobian, row, columns, center + 1, side * hat.X);
        Add(jacobian, row, columns, radius, -1);
    }
}

/// <summary>Point at a signed distance from a line's carrier (the drawn side selects the
/// sign; distance 0 is the smooth point-on-line constraint) — 1 row.</summary>
internal sealed class DistancePointLineConstraint(
    int point, int p1, int p2, double distance, double side) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var (hat, _) = UnitDirection(x, p1, p2);
        var w = Point(x, point) - Point(x, p1);
        residual[row] = side * hat.Cross(w) - distance;
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (hat, length) = UnitDirection(x, p1, p2);
        var w = Point(x, point) - Point(x, p1);
        Span<Vector2d> dHat = stackalloc Vector2d[4];
        UnitDerivatives(hat, length, dHat);
        Span<int> vars = [p1, p1 + 1, p2, p2 + 1];
        for (int k = 0; k < 4; k++)
            Add(jacobian, row, columns, vars[k], side * dHat[k].Cross(w));
        Add(jacobian, row, columns, p1, side * hat.Y);
        Add(jacobian, row, columns, p1 + 1, -side * hat.X);
        Add(jacobian, row, columns, point, -side * hat.Y);
        Add(jacobian, row, columns, point + 1, side * hat.X);
    }
}

/// <summary>Arc–arc tangency: |cA − cB| = rA + rB (external) or ±(rA − rB) (internal,
/// sign from the drawn radii) — the branch is selected by whichever the DRAWN
/// configuration is closer to. 1 row.</summary>
internal sealed class TangentArcArcConstraint(
    int centerA, int radiusA, int centerB, int radiusB, bool external, double innerSign) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        double separation = (Point(x, centerA) - Point(x, centerB)).Length;
        residual[row] = separation - (external
            ? x[radiusA] + x[radiusB]
            : innerSign * (x[radiusA] - x[radiusB]));
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var unit = UnitBetween(Point(x, centerA), Point(x, centerB));
        Add(jacobian, row, columns, centerA, unit.X);
        Add(jacobian, row, columns, centerA + 1, unit.Y);
        Add(jacobian, row, columns, centerB, -unit.X);
        Add(jacobian, row, columns, centerB + 1, -unit.Y);
        Add(jacobian, row, columns, radiusA, external ? -1 : -innerSign);
        Add(jacobian, row, columns, radiusB, external ? -1 : innerSign);
    }
}

/// <summary>Two lines of equal length — 1 row, |dA| − |dB|.</summary>
internal sealed class EqualLengthConstraint(int a1, int a2, int b1, int b2) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = (Point(x, a2) - Point(x, a1)).Length - (Point(x, b2) - Point(x, b1)).Length;

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (hatA, _) = UnitDirection(x, a1, a2);
        var (hatB, _) = UnitDirection(x, b1, b2);
        Add(jacobian, row, columns, a2, hatA.X);
        Add(jacobian, row, columns, a2 + 1, hatA.Y);
        Add(jacobian, row, columns, a1, -hatA.X);
        Add(jacobian, row, columns, a1 + 1, -hatA.Y);
        Add(jacobian, row, columns, b2, -hatB.X);
        Add(jacobian, row, columns, b2 + 1, -hatB.Y);
        Add(jacobian, row, columns, b1, hatB.X);
        Add(jacobian, row, columns, b1 + 1, hatB.Y);
    }
}

/// <summary>Two scalar variables equal (equal radii) — 1 row.</summary>
internal sealed class EqualScalarConstraint(int a, int b) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = x[a] - x[b];

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        Add(jacobian, row, columns, a, 1);
        Add(jacobian, row, columns, b, -1);
    }
}

/// <summary>One scalar variable pinned to a value (radius dimension, fixed arc radius) —
/// 1 row.</summary>
internal sealed class ScalarValueConstraint(int variable, double value) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = x[variable] - value;

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns) =>
        Add(jacobian, row, columns, variable, 1);
}

/// <summary>A point pinned to a fixed location (captured from the drawn sketch) — 2 rows.</summary>
internal sealed class FixConstraint(int point, double targetX, double targetY) : SketchConstraint
{
    public override int Rows => 2;

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        residual[row] = x[point] - targetX;
        residual[row + 1] = x[point + 1] - targetY;
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        Add(jacobian, row, columns, point, 1);
        Add(jacobian, row + 1, columns, point + 1, 1);
    }
}

/// <summary>Point-to-point distance — 1 row, |a − b| − d.</summary>
internal sealed class DistancePointsConstraint(int a, int b, double distance) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = (Point(x, a) - Point(x, b)).Length - distance;

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var unit = UnitBetween(Point(x, a), Point(x, b));
        Add(jacobian, row, columns, a, unit.X);
        Add(jacobian, row, columns, a + 1, unit.Y);
        Add(jacobian, row, columns, b, -unit.X);
        Add(jacobian, row, columns, b + 1, -unit.Y);
    }
}

/// <summary>Internal system row: an arc's endpoint joint sits exactly one radius from
/// its center — |p − c| − r. Two per arc (start, end) keep the center/radius variables
/// consistent with the exact arc geometry through any solve.</summary>
internal sealed class ArcEndpointConstraint(int point, int center, int radius) : SketchConstraint
{
    public override int Rows => 1;

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = (Point(x, point) - Point(x, center)).Length - x[radius];

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var unit = UnitBetween(Point(x, point), Point(x, center));
        Add(jacobian, row, columns, point, unit.X);
        Add(jacobian, row, columns, point + 1, unit.Y);
        Add(jacobian, row, columns, center, -unit.X);
        Add(jacobian, row, columns, center + 1, -unit.Y);
        Add(jacobian, row, columns, radius, -1);
    }
}

/// <summary>
/// The Levenberg–Marquardt engine behind <see cref="ConstrainedSketch"/> — the
/// MateSolver pattern on plain 2D length variables (no frames, no rotation encoding, so
/// no variable scaling is needed: every variable is already a length). Dense linear
/// algebra on purpose: sketches are tens of variables, and dense is honest at that
/// scale (`EngrCAD.Core.Solvers` waits for systems that earn sparsity).
/// </summary>
internal static class SketchLevenberg
{
    /// <summary>Rank threshold on SINGULAR values, relative to the largest —
    /// dimensionless, so deliberately outside the linear <see cref="Tolerance"/> ladder.
    /// The pivots of the JᵀJ factorization are squared singular values, so it is
    /// applied SQUARED. Deliberately LOOSER than MateSolver's 1e-8: squaring 1e-8 gives
    /// a 1e-16-relative pivot floor, which sits BELOW the pivoted elimination's own
    /// rounding residue at sketch sizes (measured: a rank-9 Jacobian over 14 variables
    /// reported rank 10, an arithmetic impossibility, because the eliminated Schur
    /// complement's ~2e-16-relative round-off out-ranked the floor). 1e-6 squares to
    /// 1e-12 — three decades above elimination noise, four below the smallest singular
    /// value any genuinely constrained sketch direction produces.</summary>
    internal const double RankRelativeTolerance = 1e-6;

    internal sealed record Outcome(
        bool Converged, int Iterations, int Steps, double Residual,
        double[] Solution, double[] Residuals, int Rank, int Rows);

    public static Outcome Run(
        double[] seed, IReadOnlyList<SketchConstraint> constraints, double scale,
        SketchSolverSettings settings)
    {
        int columns = seed.Length;
        int rows = 0;
        foreach (var constraint in constraints)
            rows += constraint.Rows;

        var x = (double[])seed.Clone();
        var residual = new double[rows];
        var jacobian = new double[rows * columns];

        double worst = Evaluate(constraints, x, scale, residual);
        int iteration = 0, steps = 0;
        double lambda = 1e-3;

        while (columns > 0 && worst > settings.Tolerance && iteration < settings.MaxIterations)
        {
            iteration++;
            FillJacobian(constraints, x, scale, jacobian, columns);
            var normal = new double[columns * columns];
            var gradient = new double[columns];
            NormalEquations(jacobian, residual, rows, columns, normal, gradient);

            double maxDiagonal = 0;
            for (int i = 0; i < columns; i++)
                maxDiagonal = Math.Max(maxDiagonal, normal[i * columns + i]);
            // Exact-zero semantic test: the constraints cannot move anything from here —
            // a stationary configuration (named by the caller's diagnostics).
            if (maxDiagonal <= 0)
                break;

            var before = (double[])x.Clone();
            bool accepted = false;
            for (int attempt = 0; attempt < 12 && !accepted; attempt++)
            {
                var damped = (double[])normal.Clone();
                for (int i = 0; i < columns; i++)
                    damped[i * columns + i] += lambda * maxDiagonal;

                if (!SolveSpd(damped, gradient, columns, out var step))
                {
                    lambda *= 8;
                    continue;
                }

                // The step solves A δ = Jᵀr, so descend by −δ.
                for (int i = 0; i < columns; i++)
                    x[i] = before[i] - step[i];
                double candidate = Evaluate(constraints, x, scale, residual);
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
                Array.Copy(before, x, columns);
                worst = Evaluate(constraints, x, scale, residual);
                break;   // no damping value improves it
            }
        }

        bool converged = worst <= settings.Tolerance;

        // Rank at the final configuration: how much of the sketch's freedom the
        // constraints actually see (redundant rows are counted correctly because the
        // diagonally pivoted Cholesky of JᵀJ is rank-revealing for PSD matrices).
        FillJacobian(constraints, x, scale, jacobian, columns);
        var final = new double[columns * columns];
        var unusedGradient = new double[columns];
        NormalEquations(jacobian, residual, rows, columns, final, unusedGradient);
        int rank = Rank(final, columns, RankRelativeTolerance);

        return new Outcome(converged, iteration, steps, worst, x, residual, rank, rows);
    }

    private static double Evaluate(
        IReadOnlyList<SketchConstraint> constraints, double[] x, double scale, double[] residual)
    {
        int row = 0;
        foreach (var constraint in constraints)
        {
            constraint.Residual(x, scale, residual, row);
            row += constraint.Rows;
        }
        double worst = 0;
        for (int i = 0; i < residual.Length; i++)
            worst = Math.Max(worst, Math.Abs(residual[i]));
        return worst;
    }

    private static void FillJacobian(
        IReadOnlyList<SketchConstraint> constraints, double[] x, double scale,
        double[] jacobian, int columns)
    {
        Array.Clear(jacobian);
        int row = 0;
        foreach (var constraint in constraints)
        {
            constraint.Jacobian(x, scale, jacobian, row, columns);
            row += constraint.Rows;
        }
    }

    private static void NormalEquations(
        double[] jacobian, double[] residual, int rows, int columns,
        double[] normal, double[] gradient)
    {
        for (int r = 0; r < rows; r++)
        {
            int offset = r * columns;
            double value = residual[r];
            for (int i = 0; i < columns; i++)
            {
                double ji = jacobian[offset + i];
                // Exact-zero skip: each constraint touches at most ~9 columns, so this
                // is the sparse inner loop, not a tolerance decision.
                if (ji == 0)
                    continue;
                gradient[i] += ji * value;
                int row = i * columns;
                for (int j = 0; j < columns; j++)
                    normal[row + j] += ji * jacobian[offset + j];
            }
        }
    }

    /// <summary>Cholesky solve of a symmetric positive-definite system; false when the
    /// matrix is not positive definite (the caller raises the damping and retries).</summary>
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

    /// <summary>
    /// Rank of a symmetric positive-semidefinite matrix by diagonally pivoted Cholesky
    /// (symmetric elimination on the largest remaining diagonal) — the standard
    /// rank-revealing factorization for this matrix class, and the reason redundant
    /// constraint rows do not inflate the DOF count.
    /// </summary>
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
        // Pivots are SQUARED singular values, so the relative singular-value threshold
        // enters squared.
        double floor = first * relativeTolerance * relativeTolerance;

        int rank = 0;
        for (int step = 0; step < n; step++)
        {
            int pivot = -1;
            double best = floor;
            for (int i = 0; i < n; i++)
            {
                if (live[i] && m[i * n + i] > best)
                {
                    best = m[i * n + i];
                    pivot = i;
                }
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
                {
                    if (live[j])
                        m[i * n + j] -= factor * m[pivot * n + j];
                }
            }
        }
        return rank;
    }
}
