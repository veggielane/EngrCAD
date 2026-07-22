namespace EngrCAD.Core.Spatial;

/// <summary>
/// Dynamic octree over AABB items identified by caller-supplied integer ids.
/// Items are stored at the deepest node that fully contains them; items straddling a
/// child boundary (or lying outside the root bounds) stay at the ancestor / root.
/// Prefer <see cref="Bvh"/> for static geometry — this structure exists for content
/// that changes incrementally.
/// </summary>
public sealed class Octree
{
    private sealed class OctreeNode
    {
        public required Aabb Bounds;
        public OctreeNode[]? Children;
        public List<(int Id, Aabb Box)> Items = [];
    }

    private readonly OctreeNode _root;
    private readonly int _maxDepth;
    private readonly int _maxItemsPerNode;

    public int Count { get; private set; }

    public Octree(in Aabb bounds, int maxDepth = 8, int maxItemsPerNode = 8)
    {
        if (bounds.IsEmpty) throw new ArgumentException("Octree bounds must be non-empty.", nameof(bounds));
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxItemsPerNode < 1) throw new ArgumentOutOfRangeException(nameof(maxItemsPerNode));

        _root = new OctreeNode { Bounds = bounds };
        _maxDepth = maxDepth;
        _maxItemsPerNode = maxItemsPerNode;
    }

    public void Insert(int id, in Aabb box)
    {
        Insert(_root, 1, id, box);
        Count++;
    }

    private void Insert(OctreeNode node, int depth, int id, in Aabb box)
    {
        while (true)
        {
            if (node.Children is null)
            {
                node.Items.Add((id, box));
                if (node.Items.Count > _maxItemsPerNode && depth < _maxDepth)
                    Split(node, depth);
                return;
            }

            var child = FindContainingChild(node, box);
            if (child is null)
            {
                node.Items.Add((id, box));
                return;
            }

            node = child;
            depth++;
        }
    }

    private void Split(OctreeNode node, int depth)
    {
        var c = node.Bounds.Center;
        var min = node.Bounds.Min;
        var max = node.Bounds.Max;
        node.Children = new OctreeNode[8];
        for (int i = 0; i < 8; i++)
        {
            var childMin = new Vector3d(
                (i & 1) == 0 ? min.X : c.X,
                (i & 2) == 0 ? min.Y : c.Y,
                (i & 4) == 0 ? min.Z : c.Z);
            var childMax = new Vector3d(
                (i & 1) == 0 ? c.X : max.X,
                (i & 2) == 0 ? c.Y : max.Y,
                (i & 4) == 0 ? c.Z : max.Z);
            node.Children[i] = new OctreeNode { Bounds = new Aabb(childMin, childMax) };
        }

        var items = node.Items;
        node.Items = [];
        foreach (var (id, box) in items)
        {
            var child = FindContainingChild(node, box);
            if (child is null)
                node.Items.Add((id, box));
            else
                Insert(child, depth + 1, id, box);
        }
    }

    private static OctreeNode? FindContainingChild(OctreeNode node, in Aabb box)
    {
        var c = node.Bounds.Center;
        // A box fits in exactly one octant iff it doesn't straddle any center plane.
        int index = 0;
        if (box.Min.X >= c.X) index |= 1;
        else if (box.Max.X > c.X) return null;
        if (box.Min.Y >= c.Y) index |= 2;
        else if (box.Max.Y > c.Y) return null;
        if (box.Min.Z >= c.Z) index |= 4;
        else if (box.Max.Z > c.Z) return null;

        var child = node.Children![index];
        return child.Bounds.Contains(box) ? child : null;
    }

    /// <summary>Removes an item previously inserted with the same id and box. Returns false if not found.</summary>
    public bool Remove(int id, in Aabb box)
    {
        var node = _root;
        while (true)
        {
            for (int i = 0; i < node.Items.Count; i++)
            {
                if (node.Items[i].Id == id)
                {
                    node.Items.RemoveAt(i);
                    Count--;
                    return true;
                }
            }

            if (node.Children is null)
                return false;
            var child = FindContainingChild(node, box);
            if (child is null)
                return false;
            node = child;
        }
    }

    /// <summary>Appends the ids of all items whose box intersects <paramref name="box"/>.</summary>
    public void Query(in Aabb box, List<int> results)
    {
        Query(_root, box, results);
    }

    private static void Query(OctreeNode node, in Aabb box, List<int> results)
    {
        foreach (var (id, itemBox) in node.Items)
        {
            if (itemBox.Intersects(box))
                results.Add(id);
        }

        if (node.Children is null)
            return;
        foreach (var child in node.Children)
        {
            if (child.Bounds.Intersects(box))
                Query(child, box, results);
        }
    }
}
