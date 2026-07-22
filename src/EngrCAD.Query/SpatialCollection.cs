using System.Linq.Expressions;
using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.Query;

/// <summary>
/// An immutable collection of items with a BVH over their bounds, queryable through LINQ:
/// <c>collection.AsQueryable().Where(x =&gt; x.Bounds.Within(region) &amp;&amp; ...)</c>
/// runs the spatial clause against the index in O(log n) and the residual predicate over
/// the surviving candidates only. The bounds accessor is given as an expression so the
/// provider can recognize it inside query predicates.
/// </summary>
public sealed class SpatialCollection<T> : IReadOnlyList<T>
{
    private readonly List<T> _items;
    private readonly Bvh _index;

    public Expression<Func<T, Aabb>> BoundsExpression { get; }

    /// <summary>Diagnostic: whether the most recent query execution answered from the BVH.</summary>
    public bool LastQueryUsedIndex { get; private set; }

    public SpatialCollection(IEnumerable<T> items, Expression<Func<T, Aabb>> bounds)
    {
        _items = [.. items];
        BoundsExpression = bounds;
        var boundsOf = bounds.Compile();
        var boxes = new Aabb[_items.Count];
        for (int i = 0; i < _items.Count; i++)
            boxes[i] = boundsOf(_items[i]);
        _index = Bvh.Build(boxes);
    }

    public IQueryable<T> AsQueryable() => new SpatialQueryable<T>(this);

    public int Count => _items.Count;
    public T this[int index] => _items[index];
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    internal IQueryable<T> AllAsQueryable() => _items.AsQueryable();

    internal void MarkQuery(bool usedIndex) => LastQueryUsedIndex = usedIndex;

    internal IEnumerable<T> CandidatesInBox(Aabb box)
    {
        var hits = new List<int>();
        _index.Query(box, hits);
        return hits.Select(i => _items[i]);
    }

    internal IEnumerable<T> CandidatesOnRay(Ray3d ray)
    {
        var hits = new List<int>();
        _index.Query(ray, hits);
        return hits.Select(i => _items[i]);
    }
}

public static class SpatialCollectionExtensions
{
    public static SpatialCollection<T> ToSpatialCollection<T>(
        this IEnumerable<T> items, Expression<Func<T, Aabb>> bounds) => new(items, bounds);
}
