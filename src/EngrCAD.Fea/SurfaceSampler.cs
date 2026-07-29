using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// Maps a display mesh's vertices onto an <see cref="AnalysisMesh"/>'s boundary, so a
/// solver's answer can be published on a mesh that is not the one it solved over.
///
/// <para><b>Exact where the two meshes share a vertex.</b> A display vertex whose position
/// matches an analysis boundary node <i>bit for bit</i> takes that node's value directly,
/// which covers essentially every vertex in the normal case (the same surface mesh was fed
/// to the tet mesher, and its vertices survive verbatim). Anything else — a differently
/// tessellated display mesh, or a vertex the mesher inserted around — falls back to the
/// closest point on the nearest boundary facet and interpolates there with the facet's own
/// shape functions, which is exact for a point lying on the facet and continuous across
/// facet edges.</para>
///
/// <para><b>Built once and shared, because the mapping has nothing to do with the
/// physics.</b> Displacement, von Mises, temperature and heat flux all ride the same
/// correspondence; two copies of it would be two chances for a structural plot and a
/// thermal plot of the same part to disagree about which node a display vertex is.</para>
/// </summary>
internal sealed class SurfaceSampler
{
    /// <summary>Where one display vertex takes its value from: a node, when the positions
    /// matched exactly, or a facet and barycentric coordinates on it.</summary>
    /// <param name="Node">The analysis node, or -1 when the facet fields apply.</param>
    /// <param name="Facet">The boundary facet (meaningless when <paramref name="Node"/> is
    /// non-negative).</param>
    /// <param name="L0">Barycentric coordinate at the facet's first corner.</param>
    /// <param name="L1">Barycentric coordinate at the facet's second corner.</param>
    /// <param name="L2">Barycentric coordinate at the facet's third corner.</param>
    public readonly record struct Correspondence(int Node, int Facet, double L0, double L1, double L2);

    private readonly AnalysisMesh _mesh;
    private readonly Correspondence[] _samples;

    private SurfaceSampler(AnalysisMesh mesh, Correspondence[] samples, double maxDistance)
    {
        _mesh = mesh;
        _samples = samples;
        MaxSampleDistance = maxDistance;
    }

    /// <summary>The furthest any display vertex sat from the analysis boundary. Zero means
    /// every vertex matched exactly; a value comparable to the model size means the two
    /// meshes are not the same body and the result is meaningless.</summary>
    public double MaxSampleDistance { get; }

    /// <summary>Number of display vertices mapped.</summary>
    public int Count => _samples.Length;

    /// <summary>Builds the correspondence.</summary>
    public static SurfaceSampler Build(AnalysisMesh mesh, HalfEdgeMesh displayMesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(displayMesh);
        if (mesh.FacetCount == 0)
            throw new FeaException("The analysis mesh has no boundary facets to sample from.");

        // Exact-position lookup over the analysis boundary's nodes. Exact bits, the
        // codebase's weld doctrine: two independently computed positions are not equal, and
        // two shared ones are.
        var byPosition = new Dictionary<Vector3d, int>();
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            foreach (int node in mesh.Facet(f))
                byPosition.TryAdd(mesh.Position(node), node);
        }

        var boxes = new Aabb[mesh.FacetCount];
        for (int f = 0; f < mesh.FacetCount; f++)
        {
            var facet = mesh.Facet(f);
            boxes[f] = Aabb.Empty
                .Union(mesh.Position(facet[0]))
                .Union(mesh.Position(facet[1]))
                .Union(mesh.Position(facet[2]));
        }
        var bvh = Bvh.Build(boxes);

        var samples = new Correspondence[displayMesh.VertexCount];
        double worst = 0;
        for (int v = 0; v < displayMesh.VertexCount; v++)
        {
            var p = displayMesh.GetPosition(v);
            if (byPosition.TryGetValue(p, out int node))
            {
                samples[v] = new Correspondence(node, -1, 0, 0, 0);
                continue;
            }

            // The struct-metric overload, not the delegate one: this runs once per display
            // vertex, and a closure per call is the allocation Core's own Bvh doc names
            // (measured 104 B/call -> 0 when MeshSdf made the same move). Traversal order
            // is identical, so the answer is bit-for-bit the delegate form's.
            var metric = new FacetDistance(mesh, p);
            bvh.Nearest(p, ref metric, out int nearest, out double distance);
            worst = Math.Max(worst, distance);

            var facet = mesh.Facet(nearest);
            var a = mesh.Position(facet[0]);
            var b = mesh.Position(facet[1]);
            var c = mesh.Position(facet[2]);
            var q = Distance3d.ClosestPointOnTriangle(p, a, b, c);
            var (l0, l1, l2) = Barycentric(q, a, b, c);
            samples[v] = new Correspondence(-1, nearest, l0, l1, l2);
        }

        return new SurfaceSampler(mesh, samples, worst);
    }

    /// <summary>Resamples a per-analysis-node scalar field onto the display vertices.</summary>
    public double[] Sample(IReadOnlyList<double> nodal)
    {
        var values = new double[_samples.Length];
        int perFacet = _mesh.NodesPerFacet;
        Span<double> shape = stackalloc double[6];
        for (int v = 0; v < _samples.Length; v++)
        {
            var sample = _samples[v];
            if (sample.Node >= 0)
            {
                values[v] = nodal[sample.Node];
                continue;
            }
            var facet = _mesh.Facet(sample.Facet);
            TetElement.TriangleShapeValues(
                _mesh.Order, sample.L0, sample.L1, sample.L2, shape);
            double sum = 0;
            for (int i = 0; i < perFacet; i++)
                sum += nodal[facet[i]] * shape[i];
            values[v] = sum;
        }
        return values;
    }

    /// <summary>Resamples a per-analysis-node vector field onto the display vertices.</summary>
    public Vector3d[] Sample(IReadOnlyList<Vector3d> nodal)
    {
        var values = new Vector3d[_samples.Length];
        int perFacet = _mesh.NodesPerFacet;
        Span<double> shape = stackalloc double[6];
        for (int v = 0; v < _samples.Length; v++)
        {
            var sample = _samples[v];
            if (sample.Node >= 0)
            {
                values[v] = nodal[sample.Node];
                continue;
            }
            var facet = _mesh.Facet(sample.Facet);
            TetElement.TriangleShapeValues(
                _mesh.Order, sample.L0, sample.L1, sample.L2, shape);
            var sum = Vector3d.Zero;
            for (int i = 0; i < perFacet; i++)
                sum += nodal[facet[i]] * shape[i];
            values[v] = sum;
        }
        return values;
    }

    /// <summary>Allocation-free exact distance from one query point to a boundary facet's
    /// corner triangle, for <c>Bvh.Nearest&lt;TMetric&gt;</c>.</summary>
    private readonly struct FacetDistance(AnalysisMesh mesh, Vector3d point) : IBvhDistance
    {
        public double DistanceTo(int facet)
        {
            var f = mesh.Facet(facet);
            var closest = Distance3d.ClosestPointOnTriangle(
                point, mesh.Position(f[0]), mesh.Position(f[1]), mesh.Position(f[2]));
            return closest.DistanceTo(point);
        }
    }

    private static (double L0, double L1, double L2) Barycentric(
        in Vector3d p, in Vector3d a, in Vector3d b, in Vector3d c)
    {
        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;
        double d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1);
        double d20 = v2.Dot(v0), d21 = v2.Dot(v1);
        double denominator = d00 * d11 - d01 * d01;
        if (denominator == 0)
            return (1, 0, 0); // Exact-zero guard: a degenerate facet has no interior.
        double l1 = (d11 * d20 - d01 * d21) / denominator;
        double l2 = (d00 * d21 - d01 * d20) / denominator;
        return (1.0 - l1 - l2, l1, l2);
    }
}
