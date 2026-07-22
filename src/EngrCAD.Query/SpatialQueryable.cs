using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using EngrCAD.Core;

namespace EngrCAD.Query;

/// <summary>
/// The IQueryable surface over a <see cref="SpatialCollection{T}"/>. Query execution
/// rewrites the expression tree: a Where whose predicate contains a
/// <see cref="SpatialPredicates"/> clause on the collection's registered bounds accessor
/// has its source replaced by the BVH candidate set; everything else falls back to
/// LINQ-to-Objects. The full original predicate is always re-applied, so interception is
/// purely an optimization and never changes results.
/// </summary>
internal sealed class SpatialQueryable<T> : IOrderedQueryable<T>
{
    internal SpatialCollection<T>? Root { get; }

    public SpatialQueryable(SpatialCollection<T> root)
    {
        Root = root;
        Provider = new SpatialQueryProvider<T>(root);
        Expression = Expression.Constant(this);
    }

    public SpatialQueryable(IQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }

    public IEnumerator<T> GetEnumerator() =>
        Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class SpatialQueryProvider<T>(SpatialCollection<T> collection) : IQueryProvider
{
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new SpatialQueryable<TElement>(this, expression);

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces()
            .Concat([expression.Type])
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IQueryable<>))
            .GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(
            typeof(SpatialQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

    public TResult Execute<TResult>(Expression expression) => (TResult)Execute(expression)!;

    public object? Execute(Expression expression)
    {
        collection.MarkQuery(false);
        var rewritten = new SpatialRewriter(collection).Visit(expression);
        return Expression.Lambda(rewritten).Compile().DynamicInvoke();
    }

    /// <summary>
    /// Replaces the root spatial queryable with either BVH candidates (when a Where
    /// predicate carries a recognizable spatial clause) or the full item list.
    /// </summary>
    private sealed class SpatialRewriter(SpatialCollection<T> collection) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node) =>
            node.Value is SpatialQueryable<T> { Root: { } root } && ReferenceEquals(root, collection)
                ? Expression.Constant(collection.AllAsQueryable(), typeof(IQueryable<T>))
                : base.VisitConstant(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            bool isRootWhere =
                node.Method.DeclaringType == typeof(Queryable) &&
                node.Method.Name == nameof(Queryable.Where) &&
                node.Arguments.Count == 2 &&
                node.Arguments[0] is ConstantExpression { Value: SpatialQueryable<T> { Root: { } root } } &&
                ReferenceEquals(root, collection);
            if (!isRootWhere)
                return base.VisitMethodCall(node);

            var lambda = (LambdaExpression)StripQuotes(node.Arguments[1]);
            if (!TryFindSpatialCandidates(lambda, out var candidates))
                return base.VisitMethodCall(node);

            collection.MarkQuery(true);
            // Narrow the source; keep the original predicate intact for exactness.
            var narrowed = Expression.Constant(candidates.AsQueryable(), typeof(IQueryable<T>));
            return Expression.Call(node.Method, narrowed, node.Arguments[1]);
        }

        private bool TryFindSpatialCandidates(LambdaExpression lambda, out IEnumerable<T> candidates)
        {
            var parameter = lambda.Parameters[0];
            foreach (var clause in FlattenAndAlso(lambda.Body))
            {
                if (clause is not MethodCallExpression call ||
                    call.Method.DeclaringType != typeof(SpatialPredicates))
                    continue;
                if (!StructurallyEqual(call.Arguments[0], collection.BoundsExpression.Body,
                        collection.BoundsExpression.Parameters[0], parameter))
                    continue;
                if (call.Arguments.Skip(1).Any(a => ReferencesParameter(a, parameter)))
                    continue;

                switch (call.Method.Name)
                {
                    case nameof(SpatialPredicates.Within):
                        candidates = collection.CandidatesInBox((Aabb)EvaluateConstant(call.Arguments[1])!);
                        return true;
                    case nameof(SpatialPredicates.WithinDistance):
                        var point = (Vector3d)EvaluateConstant(call.Arguments[1])!;
                        double distance = (double)EvaluateConstant(call.Arguments[2])!;
                        candidates = collection.CandidatesInBox(new Aabb(point, point).Expanded(distance));
                        return true;
                    case nameof(SpatialPredicates.HitBy):
                        candidates = collection.CandidatesOnRay((Ray3d)EvaluateConstant(call.Arguments[1])!);
                        return true;
                }
            }
            candidates = null!;
            return false;
        }

        private static IEnumerable<Expression> FlattenAndAlso(Expression expression)
        {
            if (expression is BinaryExpression binary && binary.NodeType == ExpressionType.AndAlso)
            {
                foreach (var clause in FlattenAndAlso(binary.Left))
                    yield return clause;
                foreach (var clause in FlattenAndAlso(binary.Right))
                    yield return clause;
            }
            else
            {
                yield return expression;
            }
        }

        private static Expression StripQuotes(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Quote } unary)
                expression = unary.Operand;
            return expression;
        }

        private static object? EvaluateConstant(Expression expression) =>
            Expression.Lambda(expression).Compile().DynamicInvoke();

        private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
        {
            var detector = new ParameterDetector(parameter);
            detector.Visit(expression);
            return detector.Found;
        }

        /// <summary>Structural comparison of member/method chains, mapping one parameter onto another.</summary>
        private static bool StructurallyEqual(
            Expression? a, Expression? b, ParameterExpression bParameter, ParameterExpression aParameter)
        {
            if (a is null || b is null)
                return a is null && b is null;

            return (a, b) switch
            {
                (ParameterExpression pa, ParameterExpression pb) =>
                    ReferenceEquals(pa, aParameter) && ReferenceEquals(pb, bParameter),
                (MemberExpression ma, MemberExpression mb) =>
                    ma.Member == mb.Member &&
                    StructurallyEqual(ma.Expression, mb.Expression, bParameter, aParameter),
                (MethodCallExpression ca, MethodCallExpression cb) =>
                    ca.Method == cb.Method &&
                    StructurallyEqual(ca.Object, cb.Object, bParameter, aParameter) &&
                    ca.Arguments.Count == cb.Arguments.Count &&
                    ca.Arguments.Zip(cb.Arguments).All(p => StructurallyEqual(p.First, p.Second, bParameter, aParameter)),
                (UnaryExpression ua, UnaryExpression ub) =>
                    ua.NodeType == ub.NodeType && ua.Type == ub.Type &&
                    StructurallyEqual(ua.Operand, ub.Operand, bParameter, aParameter),
                (ConstantExpression ka, ConstantExpression kb) => Equals(ka.Value, kb.Value),
                _ => false,
            };
        }

        private sealed class ParameterDetector(ParameterExpression parameter) : ExpressionVisitor
        {
            public bool Found { get; private set; }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (ReferenceEquals(node, parameter))
                    Found = true;
                return base.VisitParameter(node);
            }
        }
    }
}
