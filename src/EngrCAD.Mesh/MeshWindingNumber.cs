using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Inside/outside classification of points against a triangle mesh via the generalized
/// winding number (Jacobson, Kavan &amp; Sorkine-Hornung 2013): the sum of the signed solid
/// angles subtended by every triangle, divided by 4π. On a closed mesh the value is the
/// integer wrap count (1 inside, 0 outside, exactly — up to floating-point accumulation);
/// on an open mesh it degrades gracefully to a fractional confidence, so thresholding at ½
/// still classifies robustly near the surface of meshes with holes, self-intersections, or
/// duplicated patches, where normal- or ray-parity-based tests fail.
/// <para>
/// <see cref="WindingNumber"/> is the exact O(n) per-triangle sum.
/// <see cref="FastWindingNumber"/> is the Barill, Dickson, Schmidt, Levin &amp; Jacobson
/// 2018 "Fast Winding Numbers" approximation: triangles are clustered in a median-split
/// bounding hierarchy, and any cluster whose area-weighted center is farther from the query
/// than <see cref="Beta"/> × its radius is evaluated with a second-order multipole (dipole
/// + quadrupole) expansion of its winding field instead of per-triangle solid angles —
/// O(log n) per query in practice, with error far below the ½ decision threshold.
/// </para>
/// Construction triangulates and indexes the mesh once (the mesh may be open); queries are
/// allocation-free and thread-safe.
/// </summary>
public sealed class MeshWindingNumber
{
    private const double FourPi = 4.0 * Math.PI;
    private const int LeafSize = 8;

    // Triangle corners, SoA, permuted so every tree node owns a contiguous range.
    private readonly Vector3d[] _a;
    private readonly Vector3d[] _b;
    private readonly Vector3d[] _c;

    private struct Node
    {
        public int Left;         // index of left child (right child is Left + 1); -1 marks a leaf
        public int First;        // first triangle of the node's contiguous range
        public int Count;        // number of triangles under the node
        public Vector3d Center;  // area-weighted centroid — the multipole expansion point
        public double Radius;    // max distance from Center to any corner under the node
        public Vector3d Order1;  // Σ areaNormal (dipole coefficient)
        public Vector3d Row0;    // rows of Σ outer(centroid − Center, areaNormal)
        public Vector3d Row1;    //   (quadrupole coefficient, Barill et al. eq. 26)
        public Vector3d Row2;
    }

    private readonly Node[] _nodes;
    private readonly int _nodeCount;

    /// <summary>
    /// Far-field accuracy parameter of the fast approximation (β in Barill et al., default
    /// 2): a cluster is approximated only when the query is farther than β × cluster radius
    /// from its expansion center. Larger β is more accurate and slower.
    /// </summary>
    public double Beta { get; }

    public MeshWindingNumber(HalfEdgeMesh mesh, double beta = 2.0)
        : this(beta, mesh.Triangulated(), hierarchy: true)
    {
    }

    /// <summary>
    /// Builds over an already-triangulated mesh WITHOUT the multipole hierarchy: every
    /// query is then the exact O(triangles) solid-angle sum, and construction costs only a
    /// scan of the corners.
    /// <para>
    /// Use it when the caller makes few queries. The hierarchy costs roughly 0.7 µs per
    /// triangle to build — about thirty exact queries' worth — so below that it is pure
    /// overhead. <see cref="MeshBooleanExact"/> is the motivating case: it asks one question
    /// per surface patch, typically fewer than ten, and building two hierarchies for that
    /// was measured at 66–86% of its whole classification phase.
    /// </para>
    /// <para>
    /// <see cref="FastWindingNumber"/> and <see cref="IsInside"/> stay correct — they fall
    /// through to the exact sum — so this is a cost decision, never an accuracy one.
    /// </para>
    /// </summary>
    public static MeshWindingNumber Direct(HalfEdgeMesh triangulated) =>
        new(beta: 2.0, RequireTriangles(triangulated), hierarchy: false);

    /// <summary>
    /// Builds directly over a mesh that is already fully triangulated, skipping the
    /// triangulating copy the public constructor makes. PRECONDITION: every face of
    /// <paramref name="triangulated"/> is a triangle (validated with a cheap scan;
    /// throws <see cref="ArgumentException"/> otherwise). Use this when the caller has
    /// already paid for <see cref="HalfEdgeMesh.Triangulated"/> — e.g.
    /// <c>MeshSdf</c>'s winding-number mode — so the mesh is not rebuilt twice.
    /// </summary>
    public static MeshWindingNumber FromTriangulated(HalfEdgeMesh triangulated, double beta = 2.0) =>
        new(beta, RequireTriangles(triangulated), hierarchy: true);

    private static HalfEdgeMesh RequireTriangles(HalfEdgeMesh triangulated)
    {
        ArgumentNullException.ThrowIfNull(triangulated);
        foreach (var face in triangulated.Faces)
        {
            var h0 = face.AnyHalfEdge;
            if (h0.Next.Next.Next != h0)
                throw new ArgumentException(
                    $"Face {face.Index} is not a triangle; this entry point requires a fully triangulated mesh.",
                    nameof(triangulated));
        }
        return triangulated;
    }

    /// <summary>Core constructor; <paramref name="triangulated"/> must contain only triangles.</summary>
    private MeshWindingNumber(double beta, HalfEdgeMesh triangulated, bool hierarchy)
    {
        if (beta <= 0)
            throw new ArgumentOutOfRangeException(nameof(beta), "Beta must be positive.");
        Beta = beta;

        int n = triangulated.FaceCount;
        var a = new Vector3d[n];
        var b = new Vector3d[n];
        var c = new Vector3d[n];
        var centroids = new Vector3d[n];
        foreach (var face in triangulated.Faces)
        {
            int i = face.Index;
            var h0 = face.AnyHalfEdge;
            a[i] = h0.Origin.Position;
            b[i] = h0.Next.Origin.Position;
            c[i] = h0.Next.Next.Origin.Position;
            centroids[i] = (a[i] + b[i] + c[i]) / 3.0;
        }

        if (n == 0 || !hierarchy)
        {
            // No hierarchy: the corners stay in face order and every query is the exact sum.
            _a = a;
            _b = b;
            _c = c;
            _nodes = [];
            _nodeCount = 0;
            return;
        }

        // Median-split hierarchy over triangle centroids (same scheme as Core's Bvh, but
        // owning contiguous ranges so per-node coefficients are computed by range scans).
        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = i;
        var builder = new Builder(centroids, order, new Node[2 * n - 1]);
        builder.Subdivide(builder.AllocateNode(), 0, n);
        _nodes = builder.Nodes;
        _nodeCount = builder.NodeCount;

        // Permute corners into tree order.
        _a = new Vector3d[n];
        _b = new Vector3d[n];
        _c = new Vector3d[n];
        var areaNormals = new Vector3d[n]; // 0.5 · (b−a)×(c−a): unit normal × area
        var triCentroids = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            int t = order[i];
            _a[i] = a[t];
            _b[i] = b[t];
            _c[i] = c[t];
            areaNormals[i] = (b[t] - a[t]).Cross(c[t] - a[t]) * 0.5;
            triCentroids[i] = centroids[t];
        }

        ComputeCoefficients(areaNormals, triCentroids);
    }

    private sealed class Builder(Vector3d[] centroids, int[] order, Node[] nodes)
    {
        public Node[] Nodes { get; } = nodes;
        public int NodeCount { get; private set; }

        private readonly IComparer<int>[] _axisComparers =
        [
            Comparer<int>.Create((a, b) => centroids[a].X.CompareTo(centroids[b].X)),
            Comparer<int>.Create((a, b) => centroids[a].Y.CompareTo(centroids[b].Y)),
            Comparer<int>.Create((a, b) => centroids[a].Z.CompareTo(centroids[b].Z)),
        ];

        public int AllocateNode() => NodeCount++;

        public void Subdivide(int nodeIndex, int first, int count)
        {
            var centroidBounds = Aabb.Empty;
            for (int i = first; i < first + count; i++)
                centroidBounds = centroidBounds.Union(centroids[order[i]]);

            int axis = centroidBounds.LongestAxis;
            if (count <= LeafSize || centroidBounds.Size[axis] <= 0)
            {
                Nodes[nodeIndex] = new Node { Left = -1, First = first, Count = count };
                return;
            }

            Array.Sort(order, first, count, _axisComparers[axis]);
            int mid = first + count / 2;

            int left = AllocateNode();
            int right = AllocateNode();
            Nodes[nodeIndex] = new Node { Left = left, First = first, Count = count };
            Subdivide(left, first, mid - first);
            Subdivide(right, mid, first + count - mid);
        }
    }

    /// <summary>
    /// Fills each node's multipole data from its triangle range: expansion center = the
    /// area-weighted centroid of its triangles, dipole = Σ areaNormal, quadrupole = the
    /// Σ area·outer(centroid − center, normal) matrix, radius = max corner distance.
    /// O(n log n) total — each tree level scans all n triangles once.
    /// </summary>
    private void ComputeCoefficients(Vector3d[] areaNormals, Vector3d[] triCentroids)
    {
        for (int nodeIndex = 0; nodeIndex < _nodeCount; nodeIndex++)
        {
            ref var node = ref _nodes[nodeIndex];
            int end = node.First + node.Count;

            double areaSum = 0;
            var weighted = Vector3d.Zero;
            var plain = Vector3d.Zero;
            for (int i = node.First; i < end; i++)
            {
                double area = areaNormals[i].Length;
                areaSum += area;
                weighted += triCentroids[i] * area;
                plain += triCentroids[i];
            }
            // Degenerate (zero-area) clusters contribute nothing but still need a finite
            // center for the distance test.
            node.Center = Tolerance.Default.IsZero(areaSum)
                ? plain / node.Count
                : weighted / areaSum;

            double radiusSquared = 0;
            var order1 = Vector3d.Zero;
            Vector3d row0 = Vector3d.Zero, row1 = Vector3d.Zero, row2 = Vector3d.Zero;
            for (int i = node.First; i < end; i++)
            {
                var an = areaNormals[i];
                order1 += an;
                var d = triCentroids[i] - node.Center;
                row0 += an * d.X;
                row1 += an * d.Y;
                row2 += an * d.Z;
                radiusSquared = Math.Max(radiusSquared, _a[i].DistanceSquaredTo(node.Center));
                radiusSquared = Math.Max(radiusSquared, _b[i].DistanceSquaredTo(node.Center));
                radiusSquared = Math.Max(radiusSquared, _c[i].DistanceSquaredTo(node.Center));
            }
            node.Order1 = order1;
            node.Row0 = row0;
            node.Row1 = row1;
            node.Row2 = row2;
            node.Radius = Math.Sqrt(radiusSquared);
        }
    }

    /// <summary>
    /// Signed solid angle of triangle (a, b, c) as seen from <paramref name="p"/>
    /// (Van Oosterom &amp; Strackee 1983). Positive when the triangle's CCW front faces
    /// away from <paramref name="p"/>; a full enclosure sums to ±4π.
    /// </summary>
    public static double TriangleSolidAngle(in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d p)
    {
        var va = a - p;
        var vb = b - p;
        var vc = c - p;
        double la = va.Length, lb = vb.Length, lc = vc.Length;
        double numerator = va.Dot(vb.Cross(vc));
        double denominator = la * lb * lc + va.Dot(vb) * lc + vb.Dot(vc) * la + vc.Dot(va) * lb;
        return 2.0 * Math.Atan2(numerator, denominator);
    }

    /// <summary>Exact generalized winding number at <paramref name="point"/> — O(n).</summary>
    public double WindingNumber(in Vector3d point)
    {
        double sum = 0;
        for (int i = 0; i < _a.Length; i++)
            sum += TriangleSolidAngle(_a[i], _b[i], _c[i], point);
        return sum / FourPi;
    }

    /// <summary>
    /// Second-order fast winding number at <paramref name="point"/> (Barill et al. 2018):
    /// clusters farther than <see cref="Beta"/> × radius use their multipole expansion,
    /// near clusters descend to exact per-triangle solid angles. Instances built by
    /// <see cref="Direct"/> have no hierarchy and answer with the exact sum instead.
    /// </summary>
    public double FastWindingNumber(in Vector3d point)
    {
        if (_nodeCount == 0)
            return _a.Length == 0 ? 0 : WindingNumber(point);

        var q = point;
        double sum = 0;
        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            double farDistance = Beta * node.Radius;
            if (node.Center.DistanceSquaredTo(q) > farDistance * farDistance)
            {
                sum += EvaluateApprox(in node, q);
            }
            else if (node.Left < 0)
            {
                for (int i = node.First; i < node.First + node.Count; i++)
                    sum += TriangleSolidAngle(_a[i], _b[i], _c[i], q) / FourPi;
            }
            else
            {
                stack[top++] = node.Left;
                stack[top++] = node.Left + 1;
            }
        }
        return sum;
    }

    /// <summary>Winding-number inside test: > ½ counts as inside.</summary>
    public bool IsInside(in Vector3d point) => FastWindingNumber(point) > 0.5;

    /// <summary>
    /// Far-field winding contribution of one cluster: order1 · ∇G + order2 : ∇²G with
    /// G(q) the Green's function of the Laplacian — the dipole + quadrupole terms of the
    /// Taylor expansion of the cluster's winding field about its center.
    /// </summary>
    private static double EvaluateApprox(in Node node, in Vector3d q)
    {
        var d = node.Center - q;
        double lengthSquared = d.LengthSquared;
        double length = Math.Sqrt(lengthSquared);
        double g1 = 1.0 / (FourPi * lengthSquared * length);   // 1/(4π|d|³)
        double g2 = -3.0 * g1 / lengthSquared;                 // −3/(4π|d|⁵)

        double dipole = node.Order1.Dot(d) * g1;
        // order2 : (g1·I + g2·d⊗d) = g1·trace(order2) + g2·dᵀ·order2·d
        double trace = node.Row0.X + node.Row1.Y + node.Row2.Z;
        double dAd = d.X * node.Row0.Dot(d) + d.Y * node.Row1.Dot(d) + d.Z * node.Row2.Dot(d);
        return dipole + g1 * trace + g2 * dAd;
    }
}
