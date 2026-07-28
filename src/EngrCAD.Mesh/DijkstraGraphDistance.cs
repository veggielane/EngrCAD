using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Single/multi-source Dijkstra over the mesh's edge graph (edge weight = Euclidean
/// edge length): the standard approximate-geodesic distance (g3's
/// <c>DijkstraGraphDistance</c>), and the propagation order the discrete exponential map
/// (<see cref="MeshLocalParam"/>) consumes. Built on Core's <see cref="IndexPriorityQueue"/>
/// (decrease-key, no stale duplicates), fully deterministic: vertices are seeded and
/// relaxed in mesh order and ties resolve by the queue's fixed heap behavior.
/// </summary>
/// <remarks>
/// Graph distance overestimates the true geodesic (paths are restricted to mesh edges —
/// up to ~1.08× on a regular triangulation's worst direction, more on anisotropic
/// meshes); that is the honest contract, and the reason the class name says "graph
/// distance" rather than "geodesic".
/// </remarks>
public sealed class DijkstraGraphDistance
{
    private readonly double[] _distance;
    private readonly int[] _nearestSeed;
    private readonly int[] _predecessor;
    private readonly List<int> _settled;

    /// <summary>The mesh the distances were computed over.</summary>
    public HalfEdgeMesh Mesh { get; }

    /// <summary>The search radius given at computation (∞ = whole mesh).</summary>
    public double MaxDistance { get; }

    /// <summary>Vertices in the order they were settled — ascending distance, the exp map's upwind order.</summary>
    public IReadOnlyList<int> SettledOrder => _settled;

    private DijkstraGraphDistance(HalfEdgeMesh mesh, double maxDistance, double[] distance, int[] nearestSeed, int[] predecessor, List<int> settled)
    {
        Mesh = mesh;
        MaxDistance = maxDistance;
        _distance = distance;
        _nearestSeed = nearestSeed;
        _predecessor = predecessor;
        _settled = settled;
    }

    /// <summary>Computes distances from one seed vertex, optionally limited to a radius.</summary>
    public static DijkstraGraphDistance Compute(HalfEdgeMesh mesh, int seedVertex, double maxDistance = double.PositiveInfinity) =>
        Compute(mesh, [(seedVertex, 0.0)], maxDistance);

    /// <summary>
    /// Computes distances from multiple seeds (each with an initial distance offset —
    /// 0 for plain sources), optionally limited to a radius. Vertices whose distance
    /// would exceed <paramref name="maxDistance"/> are left unreached.
    /// </summary>
    public static DijkstraGraphDistance Compute(
        HalfEdgeMesh mesh, IEnumerable<(int Vertex, double InitialDistance)> seeds, double maxDistance = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(seeds);
        if (maxDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDistance));

        int n = mesh.VertexCount;
        var distance = new double[n];
        var nearestSeed = new int[n];
        var predecessor = new int[n];
        Array.Fill(distance, double.PositiveInfinity);
        Array.Fill(nearestSeed, -1);
        Array.Fill(predecessor, -1);

        var queue = new IndexPriorityQueue(n);
        bool anySeed = false;
        foreach (var (vertex, initial) in seeds)
        {
            if ((uint)vertex >= (uint)n)
                throw new ArgumentOutOfRangeException(nameof(seeds), $"Seed vertex {vertex} is out of range.");
            if (initial < 0)
                throw new ArgumentOutOfRangeException(nameof(seeds), "Seed distances must be non-negative.");
            anySeed = true;
            if (initial < distance[vertex])
            {
                distance[vertex] = initial;
                nearestSeed[vertex] = vertex;
                queue.EnqueueOrUpdate(vertex, initial);
            }
        }
        if (!anySeed)
            throw new ArgumentException("At least one seed vertex is required.", nameof(seeds));

        var settled = new List<int>();
        while (queue.TryDequeue(out int v, out double d))
        {
            if (d > maxDistance)
                break; // min-heap: everything remaining is at least this far
            settled.Add(v);
            foreach (var h in mesh.GetVertex(v).OutgoingHalfEdges())
            {
                int u = h.Destination.Index;
                double candidate = d + h.Length;
                if (candidate < distance[u])
                {
                    distance[u] = candidate;
                    nearestSeed[u] = nearestSeed[v];
                    predecessor[u] = v;
                    queue.EnqueueOrUpdate(u, candidate);
                }
            }
        }

        // Vertices still in the queue (or never enqueued) beyond the radius stay
        // unreached: report +∞ rather than a half-relaxed value.
        foreach (int v in Enumerable.Range(0, n))
        {
            if (distance[v] > maxDistance)
            {
                distance[v] = double.PositiveInfinity;
                nearestSeed[v] = -1;
                predecessor[v] = -1;
            }
        }

        return new DijkstraGraphDistance(mesh, maxDistance, distance, nearestSeed, predecessor, settled);
    }

    /// <summary>Graph distance to the nearest seed, or +∞ when unreached.</summary>
    public double Distance(int vertex) => _distance[vertex];

    /// <summary>Whether <paramref name="vertex"/> was reached within the radius.</summary>
    public bool IsReached(int vertex) => !double.IsPositiveInfinity(_distance[vertex]);

    /// <summary>The seed this vertex's shortest path starts at, or -1 when unreached.</summary>
    public int NearestSeed(int vertex) => _nearestSeed[vertex];

    /// <summary>The previous vertex on the shortest path (toward the seed), or -1 for seeds and unreached vertices.</summary>
    public int Predecessor(int vertex) => _predecessor[vertex];

    /// <summary>The largest finite distance any settled vertex reached.</summary>
    public double MaxReachedDistance => _settled.Count > 0 ? _distance[_settled[^1]] : double.PositiveInfinity;

    /// <summary>
    /// The shortest edge path from <paramref name="vertex"/> back to its seed, inclusive
    /// on both ends. Throws for unreached vertices.
    /// </summary>
    public IReadOnlyList<int> PathToSeed(int vertex)
    {
        if (!IsReached(vertex))
            throw new ArgumentException($"Vertex {vertex} was not reached within distance {MaxDistance}.", nameof(vertex));
        var path = new List<int> { vertex };
        int v = vertex;
        while (_predecessor[v] >= 0)
        {
            v = _predecessor[v];
            path.Add(v);
        }
        return path;
    }
}
