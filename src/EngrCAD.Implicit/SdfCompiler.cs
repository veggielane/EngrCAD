using System.Linq.Expressions;
using System.Reflection;
using EngrCAD.Core;

namespace EngrCAD.Implicit;

// Compiling an Sdf AST to a single delegate.
//
// The AST is a tree of virtual calls: every query walks it, and every node costs an
// interface dispatch plus an `in Vector3d` indirection that the JIT cannot inline through
// because the callee is chosen at run time. Flattening the tree into one expression removes
// all of that and hands the JIT one straight-line method it can schedule as a whole.
//
// THE EXACTNESS CONTRACT IS THE SAME AS THE SIMD PATH'S, and it is met the same way: each
// node emits its OWN expression, term for term, in the same association order, calling the
// same Math methods its scalar Evaluate calls. That is why SdfExpression.Build dispatches to
// a virtual on the node rather than switching over node types in this file — a switch here
// would be a second copy of every formula, free to drift from the one it claims to mirror,
// which is exactly the failure mode this repository has paid for repeatedly. A node that
// does not override the virtual emits a call back into its own Evaluate, so compilation
// always succeeds and is always exact; it simply stops paying for that subtree.
//
// WHAT IT IS WORTH (win-x64, i9-9900K, best of interleaved runs; SdfCompilerBenchmark holds
// the harness):
//
//   case                          scalar walk   compiled   batch (SIMD)
//   deep union chain, 24 nodes      12.9 Mpts/s  35.9        43.6
//   bracket CSG tree                 9.7         21.9        33.5
//   single sphere                  124.4        150.6       247.0
//
// So compilation is worth ~2.3x over the scalar walk and LOSES to the batch path, which is
// what every bulk consumer in the repository already uses (Surface Nets sampling, grid
// bakes, section contours). The honest summary: this is for callers stuck with per-point
// queries — a marching solver, a probe loop, an interactive query — and it is not a
// replacement for batching. Compiling to a VECTOR kernel would beat both and is a different
// project: it needs every node's expression written against Vector<double> instead, and the
// nodes that are deliberately scalar (gyroid, twist, bend, displace — no bit-identical
// vector transcendental) would have to stay scalar inside it anyway.

/// <summary>
/// The expression-building context handed to <see cref="Sdf.BuildExpression"/>: the current
/// query coordinates, plus the small vocabulary a node needs to spell its own formula.
/// </summary>
internal sealed class SdfExpression
{
    private static readonly MethodInfo MinMethod =
        typeof(Math).GetMethod(nameof(Math.Min), [typeof(double), typeof(double)])!;
    private static readonly MethodInfo MaxMethod =
        typeof(Math).GetMethod(nameof(Math.Max), [typeof(double), typeof(double)])!;
    private static readonly MethodInfo SqrtMethod =
        typeof(Math).GetMethod(nameof(Math.Sqrt), [typeof(double)])!;
    private static readonly MethodInfo AbsMethod =
        typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)])!;
    private static readonly MethodInfo ClampMethod =
        typeof(Math).GetMethod(nameof(Math.Clamp), [typeof(double), typeof(double), typeof(double)])!;
    private static readonly MethodInfo SinMethod =
        typeof(Math).GetMethod(nameof(Math.Sin), [typeof(double)])!;
    private static readonly MethodInfo CosMethod =
        typeof(Math).GetMethod(nameof(Math.Cos), [typeof(double)])!;
    private static readonly MethodInfo ExpMethod =
        typeof(Math).GetMethod(nameof(Math.Exp), [typeof(double)])!;
    private static readonly MethodInfo FloorMethod =
        typeof(Math).GetMethod(nameof(Math.Floor), [typeof(double)])!;

    private readonly List<ParameterExpression> _locals = [];
    private readonly List<Expression> _statements = [];

    internal SdfExpression(ParameterExpression x, ParameterExpression y, ParameterExpression z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The x coordinate at this point in the tree — already transformed by every
    /// domain operator above.</summary>
    public Expression X { get; private set; }

    /// <inheritdoc cref="X"/>
    public Expression Y { get; private set; }

    /// <inheritdoc cref="X"/>
    public Expression Z { get; private set; }

    public static Expression Const(double value) => Expression.Constant(value, typeof(double));

    public static Expression Min(Expression a, Expression b) => Expression.Call(MinMethod, a, b);
    public static Expression Max(Expression a, Expression b) => Expression.Call(MaxMethod, a, b);
    public static Expression Sqrt(Expression a) => Expression.Call(SqrtMethod, a);
    public static Expression Abs(Expression a) => Expression.Call(AbsMethod, a);
    public static Expression Sin(Expression a) => Expression.Call(SinMethod, a);
    public static Expression Cos(Expression a) => Expression.Call(CosMethod, a);
    public static Expression Exp(Expression a) => Expression.Call(ExpMethod, a);
    public static Expression Floor(Expression a) => Expression.Call(FloorMethod, a);

    public static Expression Clamp(Expression v, double lo, double hi) =>
        Expression.Call(ClampMethod, v, Const(lo), Const(hi));

    /// <summary>
    /// Left-associated sum, <c>(a + b) + c</c> — the association C# gives
    /// <c>X*X + Y*Y + Z*Z</c>, which is what <c>Vector3d.LengthSquared</c> computes and what
    /// every length in a scalar kernel therefore rounds to. Floating-point addition is not
    /// associative, so this is a correctness helper, not a convenience one.
    /// </summary>
    public static Expression Add(Expression a, Expression b, Expression c) =>
        Expression.Add(Expression.Add(a, b), c);

    /// <summary>The square of a subexpression, computed once (the local is the caller's
    /// job when the term is not already bound).</summary>
    public static Expression Square(Expression a) => Expression.Multiply(a, a);

    /// <summary>|(a, b, c)| with <c>Vector3d.Length</c>'s own association order.</summary>
    public static Expression Length3(Expression a, Expression b, Expression c) =>
        Sqrt(Add(Square(a), Square(b), Square(c)));

    /// <summary>|(a, b)| with the two-term association scalar kernels use.</summary>
    public static Expression Length2(Expression a, Expression b) =>
        Sqrt(Expression.Add(Square(a), Square(b)));

    /// <summary>
    /// Binds a subexpression to a local so it is computed once however many times a formula
    /// mentions it (a box's <c>q</c> appears six times). Straight-line only: never call this
    /// inside a conditional branch, because the assignment is hoisted ahead of the condition.
    /// </summary>
    public ParameterExpression Let(Expression value)
    {
        var local = Expression.Variable(typeof(double), $"t{_locals.Count}");
        _locals.Add(local);
        _statements.Add(Expression.Assign(local, value));
        return local;
    }

    /// <summary>Emits <paramref name="node"/> at the current coordinates.</summary>
    public Expression Build(Sdf node) => node.BuildExpression(this);

    /// <summary>
    /// Emits <paramref name="node"/> at moved coordinates — the domain operators' seam. The
    /// three expressions are bound to locals first, so a child that mentions a coordinate
    /// several times does not recompute the transform each time.
    /// </summary>
    public Expression BuildAt(Sdf node, Expression x, Expression y, Expression z)
    {
        var (sx, sy, sz) = (X, Y, Z);
        X = Let(x);
        Y = Let(y);
        Z = Let(z);
        var body = node.BuildExpression(this);
        (X, Y, Z) = (sx, sy, sz);
        return body;
    }

    /// <summary>
    /// The universal escape hatch: a call into the node's own scalar
    /// <see cref="Sdf.Evaluate(in Vector3d)"/> through a captured delegate. Exact by
    /// definition, since it IS the scalar path; it just does not flatten.
    /// </summary>
    public Expression Fallback(Sdf node)
    {
        Func<double, double, double, double> call =
            (px, py, pz) => node.Evaluate(new Vector3d(px, py, pz));
        return Expression.Invoke(Expression.Constant(call), X, Y, Z);
    }

    internal Expression Finish(Expression body) =>
        _locals.Count == 0
            ? body
            : Expression.Block(typeof(double), _locals, [.. _statements, body]);
}

/// <summary>See the file remarks for the contract and the measured numbers.</summary>
internal static class SdfCompiler
{
    public static Sdf Compile(Sdf root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var x = Expression.Parameter(typeof(double), "x");
        var y = Expression.Parameter(typeof(double), "y");
        var z = Expression.Parameter(typeof(double), "z");
        var builder = new SdfExpression(x, y, z);
        var body = builder.Finish(builder.Build(root));
        var lambda = Expression.Lambda<Func<double, double, double, double>>(body, x, y, z);
        return new CompiledSdf(root, lambda.Compile());
    }
}

/// <summary>
/// A compiled field. Bounds and the Lipschitz bound come straight from the source node —
/// compilation changes how the value is computed, never what it means.
/// </summary>
internal sealed class CompiledSdf(Sdf source, Func<double, double, double, double> compiled) : Sdf
{
    public override double Evaluate(in Vector3d p) => compiled(p.X, p.Y, p.Z);

    public override Aabb Bounds => source.Bounds;

    public override double LipschitzBound(in Aabb region) => source.LipschitzBound(region);

    /// <summary>
    /// Loops the compiled delegate. Deliberately NOT re-dispatching to the source's own
    /// batch path: that would silently undo the compilation for every bulk consumer, and a
    /// caller that wanted the SIMD path would not have compiled. The measured trade is in the
    /// file remarks — the batch path is faster, so compile only what you query per point.
    /// </summary>
    protected internal override void EvaluateBatch(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
    {
        for (int i = 0; i < x.Length; i++)
            distances[i] = compiled(x[i], y[i], z[i]);
    }

    /// <summary>Compiling twice is the same field; return this one rather than wrapping.</summary>
    internal override Expression BuildExpression(SdfExpression b) => b.Build(source);
}
