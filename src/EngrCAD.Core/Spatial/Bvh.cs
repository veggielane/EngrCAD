namespace EngrCAD.Core.Spatial;

/// <summary>
/// Exact per-item distance metric for <see cref="Bvh.Nearest{TMetric}"/>. Implement on a
/// <c>struct</c> so the constrained call devirtualizes and the query performs no heap
/// allocation (the delegate-based <see cref="Bvh.Nearest(in Vector3d, Func{int, double},
/// out int, out double)"/> overload allocates a closure per call site).
/// </summary>
public interface IBvhDistance
{
    /// <summary>Exact distance from the query point to the item with index <paramref name="item"/>.</summary>
    double DistanceTo(int item);
}

/// <summary>
/// A ray/box candidate from <see cref="Bvh.QueryAll"/>: the item index and the ray
/// parameter at which the ray enters the item's box (in units of the ray direction's
/// length, clamped at 0 for origins inside the box). Orders by entry parameter, ties
/// broken by item index for determinism.
/// </summary>
public readonly struct BvhRayHit : IComparable<BvhRayHit>
{
    public int Item { get; }
    public double TEntry { get; }

    public BvhRayHit(int item, double tEntry)
    {
        Item = item;
        TEntry = tEntry;
    }

    public int CompareTo(BvhRayHit other)
    {
        int byT = TEntry.CompareTo(other.TEntry);
        return byT != 0 ? byT : Item.CompareTo(other.Item);
    }

    public override string ToString() => $"item {Item} @ t={TEntry}";
}

/// <summary>
/// Static bounding volume hierarchy over a set of AABBs, identified by their index in the
/// span passed to <see cref="Build"/>. Construction allocates; queries are allocation-free
/// apart from growing the caller's results list. Rebuild after geometry changes — for
/// incremental updates use <see cref="Octree"/>.
/// </summary>
public sealed class Bvh
{
    private struct Node
    {
        public Aabb Bounds;
        public int Left;   // index of left child; right child is Left + 1; -1 marks a leaf
        public int First;  // first slot of the node's contiguous range in _items
        public int Count;  // number of items in the node's contiguous range
    }

    private readonly Node[] _nodes;
    private readonly int[] _items;
    private readonly Aabb[] _boxes;
    private readonly int _nodeCount;

    public int Count => _items.Length;

    public Aabb Bounds => _items.Length == 0 ? Aabb.Empty : _nodes[0].Bounds;

    /// <summary>Number of nodes in the hierarchy; <see cref="NodeView.Index"/> values are dense in [0, NodeCount). Useful for sizing per-node side data.</summary>
    public int NodeCount => _nodeCount;

    /// <summary>
    /// The original item indices permuted into tree order: every node (internal or leaf)
    /// owns the contiguous slice [<see cref="NodeView.First"/>, First + <see cref="NodeView.Count"/>)
    /// of this span, children partition their parent's range, and a leaf's slice is exactly
    /// its items. Consumers with bulk per-item data (SoA corner arrays, coefficients) can
    /// permute it into this order once and then work on contiguous ranges per node.
    /// </summary>
    public ReadOnlySpan<int> ItemsInTreeOrder => _items;

    /// <summary>The root node of the hierarchy. Throws if the tree is empty.</summary>
    public NodeView Root => _items.Length > 0
        ? new NodeView(this, 0)
        : throw new InvalidOperationException("An empty BVH has no root node.");

    /// <summary>
    /// Read-only cursor over one node of the hierarchy: bounds, the contiguous item range
    /// (see <see cref="ItemsInTreeOrder"/>), and child navigation. A zero-allocation
    /// <c>readonly struct</c>; <see cref="Index"/> is dense in [0, <see cref="Bvh.NodeCount"/>)
    /// so callers can maintain parallel per-node arrays (multipole coefficients, flags, …).
    /// </summary>
    public readonly struct NodeView
    {
        private readonly Bvh _bvh;

        /// <summary>This node's index in the hierarchy's dense node array.</summary>
        public int Index { get; }

        internal NodeView(Bvh bvh, int index)
        {
            _bvh = bvh;
            Index = index;
        }

        public Aabb Bounds => _bvh._nodes[Index].Bounds;

        public bool IsLeaf => _bvh._nodes[Index].Left < 0;

        /// <summary>First slot of this node's contiguous range in <see cref="ItemsInTreeOrder"/>.</summary>
        public int First => _bvh._nodes[Index].First;

        /// <summary>Number of items in this node's subtree (its contiguous range length).</summary>
        public int Count => _bvh._nodes[Index].Count;

        /// <summary>The original item indices in this node's subtree.</summary>
        public ReadOnlySpan<int> Items => _bvh._items.AsSpan(First, Count);

        /// <summary>Left child. Throws on leaves.</summary>
        public NodeView Left => !IsLeaf
            ? new NodeView(_bvh, _bvh._nodes[Index].Left)
            : throw new InvalidOperationException("A leaf node has no children.");

        /// <summary>Right child. Throws on leaves.</summary>
        public NodeView Right => !IsLeaf
            ? new NodeView(_bvh, _bvh._nodes[Index].Left + 1)
            : throw new InvalidOperationException("A leaf node has no children.");

        public override string ToString() =>
            IsLeaf ? $"leaf [{First}, {First + Count}) {Bounds}" : $"node #{Index} [{First}, {First + Count}) {Bounds}";
    }

    private Bvh(Node[] nodes, int nodeCount, int[] items, Aabb[] boxes)
    {
        _nodes = nodes;
        _nodeCount = nodeCount;
        _items = items;
        _boxes = boxes;
    }

    public static Bvh Build(ReadOnlySpan<Aabb> boxes, int maxLeafSize = 4)
    {
        if (maxLeafSize < 1) throw new ArgumentOutOfRangeException(nameof(maxLeafSize));

        int n = boxes.Length;
        var boxCopy = boxes.ToArray();
        var items = new int[n];
        for (int i = 0; i < n; i++)
            items[i] = i;

        if (n == 0)
            return new Bvh([], 0, items, boxCopy);

        var builder = new Builder(boxCopy, items, maxLeafSize, new Node[2 * n - 1]);
        builder.Subdivide(0, 0, n, 0);
        return new Bvh(builder.FinalNodes, builder.NodeCount, items, boxCopy);
    }

    private sealed class Builder
    {
        /// <summary>
        /// Subtrees below this size are built inline: the fork has to pay for itself, and a
        /// median split's work is linearithmic in the node's own count, so the top few levels
        /// carry most of it.
        /// </summary>
        private const int ParallelMinItems = 4096;

        private readonly Aabb[] _boxes;
        // Centroids as three contiguous double arrays rather than one Vector3d[]: the split
        // key for an axis is then a straight gather into a scratch array that Array.Sort's
        // primitive-key overload can chew through, with no delegate per comparison.
        private readonly double[] _cx, _cy, _cz;
        private readonly double[] _keys;
        private readonly int[] _items;
        private readonly int _maxLeafSize;
        private readonly int _maxParallelDepth;
        private readonly Node[] _nodes;
        private int _nodeCount = 1;   // node 0 is the root; children are allocated in pairs
        private volatile bool _forked;

        public Builder(Aabb[] boxes, int[] items, int maxLeafSize, Node[] nodes)
        {
            _boxes = boxes;
            _items = items;
            _maxLeafSize = maxLeafSize;
            _nodes = nodes;

            int n = boxes.Length;
            _cx = new double[n];
            _cy = new double[n];
            _cz = new double[n];
            for (int i = 0; i < n; i++)
            {
                var c = boxes[i].Center;
                _cx[i] = c.X;
                _cy[i] = c.Y;
                _cz[i] = c.Z;
            }
            _keys = new double[n];

            // Cap the fork-out so a Build nested inside an already-parallel caller (Scene
            // priming meshes part-by-part, say) cannot flood the pool: at most 2^depth - 1
            // extra tasks, sized to the machine.
            int cores = Environment.ProcessorCount;
            int depth = 0;
            while ((1 << depth) < cores) depth++;
            _maxParallelDepth = cores > 1 ? Math.Min(depth + 1, 8) : 0;
        }

        public int NodeCount => _nodeCount;

        /// <summary>
        /// The finished node array. When subtrees were built concurrently the allocation order
        /// depended on scheduling, so it is replayed into the canonical sequential order — the
        /// tree's SHAPE never depended on scheduling, only its node numbering, and callers
        /// key per-node side data by <see cref="NodeView.Index"/>.
        /// </summary>
        public Node[] FinalNodes => _forked ? Renumber() : _nodes;

        public void Subdivide(int nodeIndex, int first, int count, int depth)
        {
            var bounds = Aabb.Empty;
            var centroidBounds = Aabb.Empty;
            for (int i = first; i < first + count; i++)
            {
                int item = _items[i];
                bounds = bounds.Union(_boxes[item]);
                centroidBounds = centroidBounds.Union(new Vector3d(_cx[item], _cy[item], _cz[item]));
            }

            // Median split on the longest centroid axis; degenerate spread becomes a leaf.
            // The recursive in-place sort means EVERY node — internal or leaf — owns the
            // contiguous slice [first, first + count) of _items, which is what
            // ItemsInTreeOrder / NodeView expose.
            int axis = centroidBounds.LongestAxis;
            if (count <= _maxLeafSize || centroidBounds.Size[axis] <= 0)
            {
                _nodes[nodeIndex] = new Node { Bounds = bounds, Left = -1, First = first, Count = count };
                return;
            }

            var source = axis == 0 ? _cx : axis == 1 ? _cy : _cz;
            for (int i = first; i < first + count; i++)
                _keys[i] = source[_items[i]];
            Array.Sort(_keys, _items, first, count);
            int mid = first + count / 2;

            int left = Interlocked.Add(ref _nodeCount, 2) - 2;
            _nodes[nodeIndex] = new Node { Bounds = bounds, Left = left, First = first, Count = count };

            // Sibling subtrees own disjoint slices of _items and _keys and disjoint node
            // indices, so they never race.
            if (count >= ParallelMinItems && depth < _maxParallelDepth)
            {
                _forked = true;
                var right = Task.Run(() => Subdivide(left + 1, mid, first + count - mid, depth + 1));
                Subdivide(left, first, mid - first, depth + 1);
                right.GetAwaiter().GetResult();   // rethrows with the original stack, not an AggregateException
            }
            else
            {
                Subdivide(left, first, mid - first, depth + 1);
                Subdivide(left + 1, mid, first + count - mid, depth + 1);
            }
        }

        /// <summary>
        /// Re-labels nodes in the order a purely sequential build would have allocated them:
        /// pre-order DFS, both children numbered when their parent is processed. Left/right
        /// stay adjacent, so traversal — and therefore every query's result order — is
        /// untouched either way; this only makes the numbering reproducible.
        /// </summary>
        private Node[] Renumber()
        {
            var final = new Node[_nodeCount];
            Span<int> source = stackalloc int[64];
            Span<int> target = stackalloc int[64];
            int top = 0;
            source[top] = 0;
            target[top] = 0;
            top++;
            int next = 1;
            while (top > 0)
            {
                top--;
                var node = _nodes[source[top]];
                int destination = target[top];
                if (node.Left < 0)
                {
                    final[destination] = node;
                    continue;
                }
                int left = next++, right = next++;
                final[destination] = new Node
                {
                    Bounds = node.Bounds,
                    Left = left,
                    First = node.First,
                    Count = node.Count,
                };
                source[top] = node.Left + 1;
                target[top] = right;
                top++;
                source[top] = node.Left;
                target[top] = left;
                top++;
            }
            return final;
        }
    }

    /// <summary>Appends the indices of all items whose box intersects <paramref name="box"/>.</summary>
    public void Query(in Aabb box, List<int> results)
    {
        if (_items.Length == 0)
            return;

        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            if (!node.Bounds.Intersects(box))
                continue;

            if (node.Left < 0)
            {
                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    int item = _items[i];
                    if (_boxes[item].Intersects(box))
                        results.Add(item);
                }
            }
            else
            {
                stack[top++] = node.Left;
                stack[top++] = node.Left + 1;
            }
        }
    }

    /// <summary>
    /// Finds the item minimizing <paramref name="distanceTo"/> by branch-and-bound: subtrees
    /// whose bounds are farther than the best hit so far are pruned. The exact per-item
    /// distance (e.g. point-to-triangle) is the caller's; boxes only prune.
    /// Note: the delegate typically captures a closure, allocating per call — hot paths
    /// should use the <see cref="Nearest{TMetric}"/> overload with a struct metric.
    /// </summary>
    public bool Nearest(in Vector3d point, Func<int, double> distanceTo, out int nearestItem, out double nearestDistance)
    {
        var metric = new FuncDistance(distanceTo);
        return Nearest(point, ref metric, out nearestItem, out nearestDistance);
    }

    private readonly struct FuncDistance(Func<int, double> distanceTo) : IBvhDistance
    {
        public double DistanceTo(int item) => distanceTo(item);
    }

    /// <summary>
    /// Allocation-free <see cref="Nearest(in Vector3d, Func{int, double}, out int, out double)"/>:
    /// the exact per-item distance comes from a struct implementing <see cref="IBvhDistance"/>
    /// (constrained call — no boxing, no closure). Passed by <c>ref</c> so the metric may
    /// carry mutable state (e.g. caching the closest feature). Traversal order is identical
    /// to the delegate overload, so results are bit-identical for the same metric.
    /// </summary>
    public bool Nearest<TMetric>(in Vector3d point, ref TMetric metric, out int nearestItem, out double nearestDistance)
        where TMetric : struct, IBvhDistance
    {
        nearestItem = -1;
        nearestDistance = double.PositiveInfinity;
        if (_items.Length == 0)
            return false;

        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            if (node.Bounds.DistanceTo(point) >= nearestDistance)
                continue;

            if (node.Left < 0)
            {
                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    int item = _items[i];
                    double distance = metric.DistanceTo(item);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestItem = item;
                    }
                }
            }
            else
            {
                // Push the farther child first so the nearer one is explored first and
                // tightens the bound sooner.
                int left = node.Left, right = node.Left + 1;
                double dl = _nodes[left].Bounds.DistanceTo(point);
                double dr = _nodes[right].Bounds.DistanceTo(point);
                if (dl <= dr)
                {
                    stack[top++] = right;
                    stack[top++] = left;
                }
                else
                {
                    stack[top++] = left;
                    stack[top++] = right;
                }
            }
        }
        return true;
    }

    /// <summary>Appends the indices of all items whose box the ray passes through.</summary>
    public void Query(in Ray3d ray, List<int> results)
    {
        if (_items.Length == 0)
            return;

        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            if (!ray.Intersects(node.Bounds))
                continue;

            if (node.Left < 0)
            {
                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    int item = _items[i];
                    if (ray.Intersects(_boxes[item]))
                        results.Add(item);
                }
            }
            else
            {
                stack[top++] = node.Left;
                stack[top++] = node.Left + 1;
            }
        }
    }

    /// <summary>
    /// Appends every item whose box the ray passes through, ordered by ascending box entry
    /// parameter (ties by item index). Candidates only — exact primitive hits and their true
    /// t values are the caller's (a box's entry t is a lower bound on any hit inside it).
    /// If <paramref name="results"/> is non-empty, only the appended range is sorted.
    /// Implementation: collect during plain stack traversal, then in-place sort of the
    /// appended range — measured ~1.3× faster than a strict best-first PriorityQueue
    /// traversal on 500/5000-box scenes (1–12 candidates/ray), even granting the PQ a
    /// reused queue that a thread-safe API could not cache; a per-query heap would also
    /// allocate. Candidate lists are small, so the range sort is cheap.
    /// </summary>
    public void QueryAll(in Ray3d ray, List<BvhRayHit> results)
    {
        if (_items.Length == 0)
            return;

        int start = results.Count;

        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            if (!ray.Intersects(node.Bounds))
                continue;

            if (node.Left < 0)
            {
                for (int i = node.First; i < node.First + node.Count; i++)
                {
                    int item = _items[i];
                    if (ray.Intersects(_boxes[item], out double tMin, out _))
                        results.Add(new BvhRayHit(item, tMin));
                }
            }
            else
            {
                stack[top++] = node.Left;
                stack[top++] = node.Left + 1;
            }
        }

        // In-place introsort of the appended range via Comparer<BvhRayHit>.Default
        // (cached; BvhRayHit is IComparable) — no per-query allocation.
        results.Sort(start, results.Count - start, null);
    }

    /// <summary>
    /// Appends every pair (item of this tree, item of <paramref name="other"/>) whose boxes
    /// intersect — the broad-phase prefilter for tree-vs-tree work such as mesh imprint
    /// booleans. Candidate pairs only: exact primitive–primitive intersection (e.g.
    /// triangle–triangle segments) belongs to the geometry layer that owns the items.
    /// Both trees must be built in the same coordinate space. Querying a tree against
    /// itself reports each item paired with itself (and both orderings of distinct
    /// overlapping pairs); pair order within the list is deterministic but unspecified.
    /// </summary>
    public void QueryOverlap(Bvh other, List<(int Item, int OtherItem)> pairs)
    {
        if (_items.Length == 0 || other._items.Length == 0)
            return;

        // Descend one tree per step (the non-leaf with the larger-extent bounds), so the
        // stack grows at most one entry net per pop: depth ≤ depth(this) + depth(other),
        // and median-split depth is ≤ ~32 per tree. 128 is comfortably above the bound.
        Span<(int A, int B)> stack = stackalloc (int, int)[128];
        int top = 0;
        stack[top++] = (0, 0);

        while (top > 0)
        {
            var (ia, ib) = stack[--top];
            ref readonly var a = ref _nodes[ia];
            ref readonly var b = ref other._nodes[ib];
            if (!a.Bounds.Intersects(b.Bounds))
                continue;

            bool aLeaf = a.Left < 0;
            bool bLeaf = b.Left < 0;
            if (aLeaf && bLeaf)
            {
                for (int i = a.First; i < a.First + a.Count; i++)
                {
                    int itemA = _items[i];
                    ref readonly var boxA = ref _boxes[itemA];
                    for (int j = b.First; j < b.First + b.Count; j++)
                    {
                        int itemB = other._items[j];
                        if (boxA.Intersects(other._boxes[itemB]))
                            pairs.Add((itemA, itemB));
                    }
                }
            }
            else if (bLeaf || (!aLeaf && LargestExtent(a.Bounds) >= LargestExtent(b.Bounds)))
            {
                stack[top++] = (a.Left, ib);
                stack[top++] = (a.Left + 1, ib);
            }
            else
            {
                stack[top++] = (ia, b.Left);
                stack[top++] = (ia, b.Left + 1);
            }
        }
    }

    private static double LargestExtent(in Aabb box) => box.Size[box.LongestAxis];
}
