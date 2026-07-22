namespace EngrCAD.Core.Spatial;

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
        public int Left;   // index of left child; right child is Left + 1
        public int First;  // leaf: first slot in _items
        public int Count;  // > 0 marks a leaf
    }

    private readonly Node[] _nodes;
    private readonly int[] _items;
    private readonly Aabb[] _boxes;
    private readonly int _nodeCount;

    public int Count => _items.Length;

    public Aabb Bounds => _items.Length == 0 ? Aabb.Empty : _nodes[0].Bounds;

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

        var centroids = new Vector3d[n];
        for (int i = 0; i < n; i++)
            centroids[i] = boxCopy[i].Center;

        var builder = new Builder(boxCopy, centroids, items, maxLeafSize, new Node[2 * n - 1]);
        builder.Subdivide(builder.AllocateNode(), 0, n);
        return new Bvh(builder.Nodes, builder.NodeCount, items, boxCopy);
    }

    private sealed class Builder
    {
        public Node[] Nodes { get; }
        public int NodeCount { get; private set; }

        private readonly Aabb[] _boxes;
        private readonly Vector3d[] _centroids;
        private readonly int[] _items;
        private readonly int _maxLeafSize;
        private readonly IComparer<int>[] _axisComparers;

        public Builder(Aabb[] boxes, Vector3d[] centroids, int[] items, int maxLeafSize, Node[] nodes)
        {
            _boxes = boxes;
            _centroids = centroids;
            _items = items;
            _maxLeafSize = maxLeafSize;
            Nodes = nodes;
            _axisComparers =
            [
                Comparer<int>.Create((a, b) => centroids[a].X.CompareTo(centroids[b].X)),
                Comparer<int>.Create((a, b) => centroids[a].Y.CompareTo(centroids[b].Y)),
                Comparer<int>.Create((a, b) => centroids[a].Z.CompareTo(centroids[b].Z)),
            ];
        }

        public int AllocateNode() => NodeCount++;

        public void Subdivide(int nodeIndex, int first, int count)
        {
            var bounds = Aabb.Empty;
            var centroidBounds = Aabb.Empty;
            for (int i = first; i < first + count; i++)
            {
                bounds = bounds.Union(_boxes[_items[i]]);
                centroidBounds = centroidBounds.Union(_centroids[_items[i]]);
            }

            // Median split on the longest centroid axis; degenerate spread becomes a leaf.
            int axis = centroidBounds.LongestAxis;
            if (count <= _maxLeafSize || centroidBounds.Size[axis] <= 0)
            {
                Nodes[nodeIndex] = new Node { Bounds = bounds, First = first, Count = count };
                return;
            }

            Array.Sort(_items, first, count, _axisComparers[axis]);
            int mid = first + count / 2;

            int left = AllocateNode();
            int right = AllocateNode();
            Nodes[nodeIndex] = new Node { Bounds = bounds, Left = left, Count = 0 };
            Subdivide(left, first, mid - first);
            Subdivide(right, mid, first + count - mid);
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

            if (node.Count > 0)
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

            if (node.Count > 0)
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
}
