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
            // A single closed curve has no joints at all — true of a full circle and,
            // for the same structural reason, of a full ellipse.
            bool singleCircle = segments.Count == 1
                && (segments[0] is ArcSeg { IsFullCircle: true }
                    or EllipseSeg { IsFullEllipse: true });

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

                // An elliptical arc carries no centre/radius variables (see Build), so it
                // rides the chord similarity exactly as a bézier does — same reasoning,
                // and the same guarantee that both ends land on the joints.
                case EllipseSeg ellipse:
                    result.Add(RebuildEllipse(ellipse, start, end));
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

    /// <summary>
    /// The bézier rule for an elliptical arc: the centre and BOTH semi-axis vectors ride
    /// the same chord similarity, so the ellipse keeps its shape and aspect relative to
    /// its endpoints. The polar parameters are untouched because they are measured in the
    /// ellipse's OWN axis frame, which the similarity carries with it — which is also why
    /// both endpoints land back on the joints to rounding rather than by repair.
    /// </summary>
    private static EllipseSeg RebuildEllipse(EllipseSeg ellipse, Vector2d start, Vector2d end)
    {
        var oldStart = ellipse.Start;
        var oldChord = ellipse.End - oldStart;
        var newChord = end - start;
        if (!(oldChord.LengthSquared > 0))   // exact-zero division guard: a closed arc translates
        {
            var delta = start - oldStart;
            return new EllipseSeg(
                ellipse.Center + delta, ellipse.SemiAxisX, ellipse.SemiAxisY,
                ellipse.StartAngle, ellipse.Sweep);
        }
        var m = ComplexDivide(newChord, oldChord);
        return new EllipseSeg(
            start + ComplexMultiply(m, ellipse.Center - oldStart),
            ComplexMultiply(m, ellipse.SemiAxisX),
            ComplexMultiply(m, ellipse.SemiAxisY),
            ellipse.StartAngle, ellipse.Sweep);
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
/// A point lies on the CARRIER of an elliptical arc — the whole ellipse, exactly as
/// <c>PointOn(point, arc)</c> takes the whole circle.
///
/// <para><b>The filed premise was wrong, and the correction is the design.</b> The backlog
/// entry said a point-on-ellipse residual "would need its own foot parameter as a VARIABLE"
/// because an ellipse's nearest point is a quartic root. True of the DISTANCE, and a
/// constraint does not need the distance: an ellipse is a CONIC, so membership has a closed
/// algebraic form. With centre C and semi-axis VECTORS A and B (the representation
/// <c>EllipseSeg</c> already carries), M = [A B] maps the unit circle onto it, so
/// <c>|M⁻¹(p − C)| − 1</c> is signed, zero exactly on the ellipse, and first order
/// everywhere but the centre — which is the circle's own residual <c>|p − c| − r</c> with
/// the radius replaced by a matrix, and reduces to it bit for bit when A = (r, 0),
/// B = (0, r).</para>
///
/// <para><b>Where the carrier's own variables come from.</b> An elliptical arc carries no
/// centre or axis variables — it rides the chord SIMILARITY of its two joints, exactly as a
/// bézier does. That is not an obstacle here but the simplification: the quantity
/// <c>M⁻¹(p − C)</c> is invariant under a similarity, so it can be evaluated once and for
/// all against the DRAWN ellipse in the normalized chord coordinate ζ = (p − s)/(e − s),
/// whose derivatives in p, s and e are one complex division each. A FULL ellipse loop has
/// no joints at all, so its carrier is fixed and only the point moves — still first order,
/// and stated rather than special-cased.</para>
///
/// <para>The row is scaled by the drawn ellipse's own geometric-mean semi-axis so it is a
/// LENGTH like every other residual here; for a circle that scaling makes it exactly the
/// radial displacement.</para>
/// </summary>
internal sealed class PointOnEllipseConstraint : SketchConstraint
{
    private readonly int _point;
    private readonly int _start;      // −1 when the carrier is fixed (a full-ellipse loop)
    private readonly int _end;
    private readonly Vector2d _chord;    // e0 − s0 in the drawn sketch
    private readonly Vector2d _offset;   // C0 − s0
    private readonly Vector2d _fixedCenter;
    private readonly double _m00, _m01, _m10, _m11;   // M0⁻¹, row major
    private readonly double _scale;

    public PointOnEllipseConstraint(
        int point, int start, int end, in Vector2d drawnStart, in Vector2d drawnEnd,
        in Vector2d center, in Vector2d semiAxisX, in Vector2d semiAxisY)
    {
        _point = point;
        _start = start;
        _end = end;
        _chord = drawnEnd - drawnStart;
        _offset = center - drawnStart;
        _fixedCenter = center;
        // M = [A B]; its inverse is adj(M)/det(M), and det is nonzero for a real ellipse.
        double determinant = semiAxisX.X * semiAxisY.Y - semiAxisY.X * semiAxisX.Y;
        _m00 = semiAxisY.Y / determinant;
        _m01 = -semiAxisY.X / determinant;
        _m10 = -semiAxisX.Y / determinant;
        _m11 = semiAxisX.X / determinant;
        _scale = Math.Sqrt(semiAxisX.Length * semiAxisY.Length);
    }

    public override int Rows => 1;

    /// <summary>Whether the carrier moves with the sketch at all.</summary>
    private bool Fixed => _start < 0;

    private Vector2d Normalized(double[] x)
    {
        var p = Point(x, _point);
        if (Fixed)
            return p - _fixedCenter;
        var s = Point(x, _start);
        var chord = Point(x, _end) - s;
        // Exact-zero division guard: a collapsed chord leaves the carrier undefined, and
        // reporting the drawn offset makes the row constant rather than NaN.
        if (!(chord.LengthSquared > 0))
            return p - _fixedCenter;
        var zeta = ComplexDivide(p - s, chord);
        return ComplexMultiply(zeta, _chord) - _offset;
    }

    private Vector2d Mapped(in Vector2d v) => new(_m00 * v.X + _m01 * v.Y, _m10 * v.X + _m11 * v.Y);

    public override void Residual(double[] x, double scale, double[] residual, int row) =>
        residual[row] = _scale * (Mapped(Normalized(x)).Length - 1);

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var q = Mapped(Normalized(x));
        double length = q.Length;
        // Exact-zero guard: at the ellipse's own centre the residual has no gradient
        // DIRECTION — the same singularity PointOn(point, arc) refuses at an arc's centre,
        // caught there so a caller is told rather than left with a stalled solve.
        if (!(length > 0))
            return;
        var unit = q / length;
        // grad_v r = _scale · R_conj(f)ᵀ-chain: dq/dv = M0⁻¹ R_f, so ∂r/∂v = (M0⁻ᵀ q̂)ᵀ R_f,
        // and R_fᵀ g is the complex product conj(f)·g.
        var g = new Vector2d(_m00 * unit.X + _m10 * unit.Y, _m01 * unit.X + _m11 * unit.Y);

        if (Fixed)
        {
            Add(jacobian, row, columns, _point, _scale * g.X);
            Add(jacobian, row, columns, _point + 1, _scale * g.Y);
            return;
        }

        var p = Point(x, _point);
        var s = Point(x, _start);
        var e = Point(x, _end);
        var chord = e - s;
        if (!(chord.LengthSquared > 0))
            return;
        var inverseChord = ComplexDivide(new Vector2d(1, 0), chord);
        var inverseChordSquared = ComplexMultiply(inverseChord, inverseChord);
        // ζ = (p − s)/(e − s): ∂ζ/∂p = 1/(e−s), ∂ζ/∂s = (p−e)/(e−s)², ∂ζ/∂e = −(p−s)/(e−s)².
        Contribute(jacobian, row, columns, _point, ComplexMultiply(_chord, inverseChord), g);
        Contribute(jacobian, row, columns, _start,
            ComplexMultiply(_chord, ComplexMultiply(p - e, inverseChordSquared)), g);
        Contribute(jacobian, row, columns, _end,
            ComplexMultiply(_chord, ComplexMultiply(s - p, inverseChordSquared)), g);
    }

    private void Contribute(
        double[] jacobian, int row, int columns, int variable, in Vector2d factor, in Vector2d g)
    {
        var grad = ComplexMultiply(new Vector2d(factor.X, -factor.Y), g);
        Add(jacobian, row, columns, variable, _scale * grad.X);
        Add(jacobian, row, columns, variable + 1, _scale * grad.Y);
    }

    internal static Vector2d ComplexMultiply(in Vector2d a, in Vector2d b) =>
        new(a.X * b.X - a.Y * b.Y, a.X * b.Y + a.Y * b.X);

    internal static Vector2d ComplexDivide(in Vector2d a, in Vector2d b) =>
        ComplexMultiply(a, new Vector2d(b.X, -b.Y)) / b.LengthSquared;
}

/// <summary>
/// A point lies on a cubic BÉZIER — the one carrier in the sketch vocabulary with no closed
/// algebraic membership test, so this is the standard treatment the backlog named: the foot
/// parameter t joins the system as its own VARIABLE and the residual is the vector equation
/// <c>B(t) − p = 0</c> (two rows), whose Jacobian is exact in every unknown.
///
/// <para><b>Two rows and one variable removes exactly one degree of freedom</b>, which is
/// what a point-on-curve constraint means, so the solver's DOF report needs no special
/// case.</para>
///
/// <para><b>The drawn foot is the seed AND the branch selector</b>, the same rule the whole
/// constraint layer runs on: a bézier can pass a point three times, and which crossing the
/// constraint means is read off the drawing rather than guessed. The parameter is NOT
/// clamped to [0, 1] — clamping would be a branch selector in disguise and would put a
/// kink in the residual — so the constraint is on the carrier's own polynomial, exactly as
/// <c>PointOn(point, line)</c> is on the infinite line and <c>PointOn(point, arc)</c> on the
/// whole circle.</para>
///
/// <para>The control points ride the chord similarity, which makes the curve
/// <c>B(t) = s + (e − s)·β(t)</c> for a fixed complex-valued β read off the DRAWN offsets —
/// so both moving joints enter through one complex multiply and the derivatives are
/// one-liners.</para>
/// </summary>
internal sealed class PointOnBezierConstraint : SketchConstraint
{
    private readonly int _point;
    private readonly int _start;
    private readonly int _end;
    private readonly int _foot;
    private readonly Vector2d _a1, _a2;   // control offsets from the drawn start, over the drawn chord

    public PointOnBezierConstraint(
        int point, int start, int end, int foot, in Vector2d drawnStart, in Vector2d drawnEnd,
        in Vector2d control1, in Vector2d control2)
    {
        _point = point;
        _start = start;
        _end = end;
        _foot = foot;
        var chord = drawnEnd - drawnStart;
        _a1 = PointOnEllipseConstraint.ComplexDivide(control1 - drawnStart, chord);
        _a2 = PointOnEllipseConstraint.ComplexDivide(control2 - drawnStart, chord);
    }

    public override int Rows => 2;

    /// <summary>β(t) and β′(t): the drawn curve expressed over its own chord, so that
    /// <c>B(t) = s + (e − s)·β(t)</c> holds for any placement of the joints.</summary>
    private (Vector2d Value, Vector2d Derivative) Beta(double t)
    {
        double u = 1 - t;
        var value = _a1 * (3 * u * u * t) + _a2 * (3 * u * t * t) + new Vector2d(t * t * t, 0);
        var derivative = _a1 * (3 * u * (u - 2 * t)) + _a2 * (3 * t * (2 * u - t))
            + new Vector2d(3 * t * t, 0);
        return (value, derivative);
    }

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var s = Point(x, _start);
        var chord = Point(x, _end) - s;
        var (beta, _) = Beta(x[_foot]);
        var on = s + PointOnEllipseConstraint.ComplexMultiply(chord, beta);
        var delta = on - Point(x, _point);
        residual[row] = delta.X;
        residual[row + 1] = delta.Y;
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var s = Point(x, _start);
        var chord = Point(x, _end) - s;
        var (beta, dBeta) = Beta(x[_foot]);

        // d/dt: (e − s)·β′(t).
        var dt = PointOnEllipseConstraint.ComplexMultiply(chord, dBeta);
        Add(jacobian, row, columns, _foot, dt.X);
        Add(jacobian, row + 1, columns, _foot, dt.Y);

        // d/dp: −I.
        Add(jacobian, row, columns, _point, -1);
        Add(jacobian, row + 1, columns, _point + 1, -1);

        // d/ds: I − R_beta;  d/de: R_beta — complex multiplication by β as a 2×2 block.
        Add(jacobian, row, columns, _start, 1 - beta.X);
        Add(jacobian, row, columns, _start + 1, beta.Y);
        Add(jacobian, row + 1, columns, _start, -beta.Y);
        Add(jacobian, row + 1, columns, _start + 1, 1 - beta.X);

        Add(jacobian, row, columns, _end, beta.X);
        Add(jacobian, row, columns, _end + 1, -beta.Y);
        Add(jacobian, row + 1, columns, _end, beta.Y);
        Add(jacobian, row + 1, columns, _end + 1, beta.X);
    }
}

/// <summary>
/// The tangent DIRECTION a bézier or an elliptical arc leaves one of its joints in, held
/// parallel (or perpendicular) to a line — the tangency the vocabulary lacked for those two
/// segment kinds.
///
/// <para>Both ride the chord similarity, so the tangent at a joint is a FIXED complex
/// multiple of the live chord: a cubic leaves its start along <c>3(C1 − P0)</c> and arrives
/// at its end along <c>3(P3 − C2)</c>, an elliptical arc leaves along its own derivative
/// there, and each of those is the drawn value times (e − s)/(e₀ − s₀). So the constraint is
/// the ordinary two-direction cross/dot product with one direction supplied by a constant
/// times the chord — the same row <see cref="DirectionConstraint"/> writes, with the curve's
/// fixed factor folded in.</para>
/// </summary>
internal sealed class CurveTangentConstraint(
    int start, int end, Vector2d factor, int lineP1, int lineP2, bool parallel)
    : SketchConstraint
{
    public override int Rows => 1;

    private Vector2d Direction(double[] x) =>
        PointOnEllipseConstraint.ComplexMultiply(Point(x, end) - Point(x, start), factor);

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var (line, _) = UnitDirection(x, lineP1, lineP2);
        var curve = Direction(x);
        double length = curve.Length;
        if (!(length > 0))
        {
            residual[row] = 0;
            return;
        }
        var hat = curve / length;
        residual[row] = scale * (parallel ? hat.Cross(line) : hat.Dot(line));
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (line, lineLength) = UnitDirection(x, lineP1, lineP2);
        var curve = Direction(x);
        double length = curve.Length;
        if (!(length > 0))
            return;
        var hat = curve / length;

        // The line's own columns: the (I − d̂d̂ᵀ)/|d| projection the direction rows all use.
        Span<Vector2d> dLine = stackalloc Vector2d[4];
        UnitDerivatives(line, lineLength, dLine);
        var against = parallel ? new Vector2d(-hat.Y, hat.X) : hat;
        foreach (var (variable, index) in (ReadOnlySpan<(int, int)>)
                 [(lineP1, 0), (lineP1 + 1, 1), (lineP2, 2), (lineP2 + 1, 3)])
        {
            Add(jacobian, row, columns, variable, scale * against.Dot(dLine[index]));
        }

        // The curve's: d(hat)/d(chord) is the same projection, composed with the constant
        // complex factor, and the chord's own derivative is −I at the start and +I at the end.
        var target = parallel ? new Vector2d(line.Y, -line.X) : line;
        var projected = (target - hat * hat.Dot(target)) / length;
        var back = PointOnEllipseConstraint.ComplexMultiply(
            new Vector2d(factor.X, -factor.Y), projected);
        Add(jacobian, row, columns, start, -scale * back.X);
        Add(jacobian, row, columns, start + 1, -scale * back.Y);
        Add(jacobian, row, columns, end, scale * back.X);
        Add(jacobian, row, columns, end + 1, scale * back.Y);
    }
}

/// <summary>
/// A line is TANGENT to an elliptical arc's carrier conic — the tangency the vocabulary
/// lacked for that segment kind, and it needs no foot parameter at all.
///
/// <para><b>The residual is the line's own signed distance from the ellipse's support.</b>
/// Writing the conic as <c>p(u) = C + M u</c> with <c>M = [A B]</c> and <c>|u| = 1</c>, the
/// signed distance from a point of it to a line with unit normal n̂ through q is
/// <c>n̂·(C − q) + (Mᵀn̂)·u</c>, whose extremes over the unit circle are
/// <c>n̂·(C − q) ± |Mᵀn̂|</c>. Tangency is one extreme vanishing, so
/// <c>r = n̂·(C − q) − σ·|Mᵀn̂|</c> — ONE row, SIGNED, first order everywhere (|Mᵀn̂| is a
/// hypotenuse of two linear functions of the direction and never vanishes for a real
/// ellipse), and no new unknown. It reduces to the circular
/// <see cref="TangentLineArcConstraint"/> exactly when A and B are perpendicular and
/// equal, since |Mᵀn̂| is then the radius whatever the direction.</para>
///
/// <para><b>σ is the SIDE, read off the drawing</b>, which is the branch selector the whole
/// constraint layer runs on: a line has two tangents to a conic and which one is meant is a
/// property of the sketch as drawn, not something to guess. A line drawn THROUGH the centre
/// has no side, and the caller is told so rather than left with a solve that converges onto
/// whichever tangent round-off prefers.</para>
///
/// <para><b>It is evaluated in the DRAWN frame, which is what makes the ellipse constant.</b>
/// The carrier rides the chord similarity, and a similarity maps lines to lines and
/// preserves tangency — so pulling the LINE back through it (the same
/// <c>ζ = (p − s)/(e − s)</c> the point-on-ellipse row uses) leaves the conic's centre and
/// both semi-axes as compile-time constants and puts every unknown in the two pulled-back
/// line points. The residual is then a length in the drawn frame's units.</para>
/// </summary>
internal sealed class TangentLineEllipseConstraint : SketchConstraint
{
    private readonly int _p1, _p2;
    private readonly int _start;      // −1 when the carrier is fixed (a full-ellipse loop)
    private readonly int _end;
    private readonly Vector2d _chord;     // e₀ − s₀ in the drawn sketch
    private readonly Vector2d _drawnStart;
    private readonly Vector2d _center, _semiX, _semiY;
    private readonly double _side;

    public TangentLineEllipseConstraint(
        int p1, int p2, int start, int end, in Vector2d drawnStart, in Vector2d drawnEnd,
        in Vector2d center, in Vector2d semiAxisX, in Vector2d semiAxisY, double side)
    {
        _p1 = p1;
        _p2 = p2;
        _start = start;
        _end = end;
        _drawnStart = drawnStart;
        _chord = drawnEnd - drawnStart;
        _center = center;
        _semiX = semiAxisX;
        _semiY = semiAxisY;
        _side = side;
    }

    public override int Rows => 1;

    private bool Fixed => _start < 0;

    /// <summary>A live point pulled back into the drawn frame (the identity when the
    /// carrier cannot move).</summary>
    private Vector2d Pull(double[] x, int variable, in Vector2d chord, in Vector2d s)
    {
        var p = Point(x, variable);
        if (Fixed || !(chord.LengthSquared > 0))
            return p;
        var zeta = PointOnEllipseConstraint.ComplexDivide(p - s, chord);
        return PointOnEllipseConstraint.ComplexMultiply(zeta, _chord) + _drawnStart;
    }

    private (Vector2d U, Vector2d V, Vector2d Chord, Vector2d S) Frame(double[] x)
    {
        var s = Fixed ? default : Point(x, _start);
        var chord = Fixed ? default : Point(x, _end) - s;
        return (Pull(x, _p1, chord, s), Pull(x, _p2, chord, s), chord, s);
    }

    public override void Residual(double[] x, double scale, double[] residual, int row)
    {
        var (u, v, _, _) = Frame(x);
        var d = v - u;
        double length = d.Length;
        if (!(length > 0))
        {
            residual[row] = 0;
            return;
        }
        var unit = d / length;
        residual[row] = (_center - u).Cross(unit)
            - _side * Math.Sqrt(Square(_semiX.Cross(unit)) + Square(_semiY.Cross(unit)));
    }

    public override void Jacobian(double[] x, double scale, double[] jacobian, int row, int columns)
    {
        var (u, v, chord, s) = Frame(x);
        var d = v - u;
        double length = d.Length;
        if (!(length > 0))
            return;
        var unit = d / length;
        var w = _center - u;

        // Each of f, alpha, beta is cross(k, d)/|d| for a constant k, so all three share one
        // derivative form: d/dd [cross(k, d)/|d|] = ((−k.Y, k.X) − cross(k, d̂)·d̂)/|d|.
        double f = w.Cross(unit), alpha = _semiX.Cross(unit), beta = _semiY.Cross(unit);
        double support = Math.Sqrt(Square(alpha) + Square(beta));
        var df = (new Vector2d(-w.Y, w.X) - unit * f) / length;
        var gradient = df;
        if (support > 0)
        {
            var dAlpha = (new Vector2d(-_semiX.Y, _semiX.X) - unit * alpha) / length;
            var dBeta = (new Vector2d(-_semiY.Y, _semiY.X) - unit * beta) / length;
            gradient -= (dAlpha * alpha + dBeta * beta) * (_side / support);
        }

        // d = v − u, and u also enters through w = C − u.
        var gradientU = new Vector2d(-unit.Y, unit.X) - gradient;
        var gradientV = gradient;

        if (Fixed)
        {
            Add(jacobian, row, columns, _p1, gradientU.X);
            Add(jacobian, row, columns, _p1 + 1, gradientU.Y);
            Add(jacobian, row, columns, _p2, gradientV.X);
            Add(jacobian, row, columns, _p2 + 1, gradientV.Y);
            return;
        }

        if (!(chord.LengthSquared > 0))
            return;
        var p1 = Point(x, _p1);
        var p2 = Point(x, _p2);
        var e = Point(x, _end);
        var inverseChord = PointOnEllipseConstraint.ComplexDivide(new Vector2d(1, 0), chord);
        var inverseChordSquared = PointOnEllipseConstraint.ComplexMultiply(inverseChord, inverseChord);
        var toDrawn = _chord;

        // zeta = (p − s)/(e − s): d(zeta)/dp = 1/(e−s), d/ds = (p−e)/(e−s)², d/de = (s−p)/(e−s)².
        Contribute(jacobian, row, columns, _p1,
            PointOnEllipseConstraint.ComplexMultiply(toDrawn, inverseChord), gradientU);
        Contribute(jacobian, row, columns, _p2,
            PointOnEllipseConstraint.ComplexMultiply(toDrawn, inverseChord), gradientV);
        // BOTH pulled points move with the carrier's own joints.
        Contribute(jacobian, row, columns, _start, PointOnEllipseConstraint.ComplexMultiply(
            toDrawn, PointOnEllipseConstraint.ComplexMultiply(p1 - e, inverseChordSquared)), gradientU);
        Contribute(jacobian, row, columns, _start, PointOnEllipseConstraint.ComplexMultiply(
            toDrawn, PointOnEllipseConstraint.ComplexMultiply(p2 - e, inverseChordSquared)), gradientV);
        Contribute(jacobian, row, columns, _end, PointOnEllipseConstraint.ComplexMultiply(
            toDrawn, PointOnEllipseConstraint.ComplexMultiply(s - p1, inverseChordSquared)), gradientU);
        Contribute(jacobian, row, columns, _end, PointOnEllipseConstraint.ComplexMultiply(
            toDrawn, PointOnEllipseConstraint.ComplexMultiply(s - p2, inverseChordSquared)), gradientV);
    }

    private static double Square(double v) => v * v;

    private static void Contribute(
        double[] jacobian, int row, int columns, int variable, in Vector2d factor, in Vector2d g)
    {
        var grad = PointOnEllipseConstraint.ComplexMultiply(new Vector2d(factor.X, -factor.Y), g);
        Add(jacobian, row, columns, variable, grad.X);
        Add(jacobian, row, columns, variable + 1, grad.Y);
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
