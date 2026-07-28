using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Local surface parameterization by a discrete exponential map (Schmidt, Grimm &amp;
/// Wyvill 2006): per-vertex (u, v) coordinates around a seed vertex within a geodesic
/// radius — the enabler for decal placement, engraving and wrapping flat artwork onto
/// curved surfaces. Vertices are visited in Dijkstra order
/// (<see cref="DijkstraGraphDistance"/>) and each takes the <b>upwind average</b> of its
/// already-mapped neighbours' predictions: neighbour k predicts
/// uv_k + (p_v − p_k) expressed in k's parallel-transported tangent frame, and the seed
/// frame is transported vertex-to-vertex by the minimal rotation between vertex normals.
/// </summary>
/// <remarks>
/// <para>
/// The map is exact on a plane (transport is the identity and the predictions
/// telescope), near-exact on developable surfaces (a cylinder unrolls), and distorts
/// where Gaussian curvature concentrates — on a sphere cap the radial coordinate tracks
/// the geodesic distance while a full ring's circumference is genuinely shorter than
/// 2π·r(geodesic), which no map can avoid. Tests pin all three behaviours.
/// </para>
/// <para>
/// Deterministic: the visit order is Dijkstra's settle order, neighbour contributions
/// accumulate in ring order, and no randomness exists anywhere.
/// </para>
/// </remarks>
public sealed class MeshLocalParam
{
    private readonly Vector2d[] _uv;
    private readonly bool[] _hasUv;
    private readonly List<int> _mapped;

    /// <summary>The mesh the parameterization was computed over.</summary>
    public HalfEdgeMesh Mesh { get; }

    /// <summary>The seed vertex (uv = (0, 0)).</summary>
    public int SeedVertex { get; }

    /// <summary>The geodesic (graph-distance) radius the map covers.</summary>
    public double MaxRadius { get; }

    private MeshLocalParam(HalfEdgeMesh mesh, int seed, double maxRadius, Vector2d[] uv, bool[] hasUv, List<int> mapped)
    {
        Mesh = mesh;
        SeedVertex = seed;
        MaxRadius = maxRadius;
        _uv = uv;
        _hasUv = hasUv;
        _mapped = mapped;
    }

    /// <summary>Whether <paramref name="vertex"/> lies within the mapped radius.</summary>
    public bool HasUv(int vertex) => _hasUv[vertex];

    /// <summary>The vertex's (u, v) coordinates. Throws for unmapped vertices.</summary>
    public Vector2d Uv(int vertex) =>
        _hasUv[vertex]
            ? _uv[vertex]
            : throw new ArgumentException($"Vertex {vertex} is outside the mapped radius {MaxRadius}.", nameof(vertex));

    /// <summary>The mapped vertices, in the Dijkstra settle order they were assigned.</summary>
    public IReadOnlyList<int> MappedVertices => _mapped;

    /// <summary>
    /// Computes the exponential map around <paramref name="seedVertex"/> out to graph
    /// distance <paramref name="maxRadius"/>. <paramref name="referenceDirection"/>
    /// (projected into the seed's tangent plane) becomes the +u axis; when omitted an
    /// arbitrary perpendicular of the seed normal is used — stable per mesh, but not
    /// meaningful, so pass one whenever the orientation matters (placing a decal).
    /// </summary>
    public static MeshLocalParam Compute(
        HalfEdgeMesh mesh, int seedVertex, double maxRadius, Vector3d? referenceDirection = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if ((uint)seedVertex >= (uint)mesh.VertexCount)
            throw new ArgumentOutOfRangeException(nameof(seedVertex));
        if (!(maxRadius > 0))
            throw new ArgumentOutOfRangeException(nameof(maxRadius), "The map needs a positive radius.");

        var triangulated = mesh.Triangulated(); // vertex indices preserved
        var normals = triangulated.ComputeVertexNormals();
        int n = triangulated.VertexCount;

        var seedNormal = normals[seedVertex];
        if (seedNormal == Vector3d.Zero)
            throw new ArgumentException($"Seed vertex {seedVertex} has no usable normal (degenerate ring).", nameof(seedVertex));

        // Seed tangent frame: +u from the reference direction projected to the tangent
        // plane, +v completing a right-handed frame about the normal.
        Vector3d e1;
        if (referenceDirection is { } reference)
        {
            var projected = reference - seedNormal * reference.Dot(seedNormal);
            if (!projected.TryNormalize(Tolerance.Default, out e1))
                throw new ArgumentException(
                    "The reference direction is parallel to the seed normal and projects to nothing.",
                    nameof(referenceDirection));
        }
        else
        {
            e1 = seedNormal.ArbitraryPerpendicular(Tolerance.Default);
        }

        var dijkstra = DijkstraGraphDistance.Compute(triangulated, seedVertex, maxRadius);

        var uv = new Vector2d[n];
        var hasUv = new bool[n];
        // Transported frame per mapped vertex: t1/t2 span the vertex tangent plane and
        // represent the seed's e1/e2 parallel-transported along the surface.
        var t1 = new Vector3d[n];
        var t2 = new Vector3d[n];
        var mapped = new List<int>();

        foreach (int v in dijkstra.SettledOrder)
        {
            if (v == seedVertex)
            {
                uv[v] = Vector2d.Zero;
                t1[v] = e1;
                t2[v] = seedNormal.Cross(e1);
                hasUv[v] = true;
                mapped.Add(v);
                continue;
            }

            // Upwind average over already-mapped neighbours, inverse-distance weighted.
            double weightSum = 0;
            var uvSum = Vector2d.Zero;
            var t1Sum = Vector3d.Zero;
            var pv = triangulated.GetPosition(v);
            foreach (var h in triangulated.GetVertex(v).OutgoingHalfEdges())
            {
                int k = h.Destination.Index;
                if (!hasUv[k])
                    continue;
                var pk = triangulated.GetPosition(k);
                var d = pv - pk;
                double length = d.Length;
                if (length == 0)
                    continue; // exact-zero guard: duplicate positions carry no direction
                double w = 1.0 / length;

                // Neighbour k's prediction: its uv plus the step expressed in its frame.
                uvSum += (uv[k] + new Vector2d(d.Dot(t1[k]), d.Dot(t2[k]))) * w;
                // Transport k's frame toward v by the minimal rotation n_k -> n_v.
                t1Sum += RotateBetween(normals[k], normals[v], t1[k]) * w;
                weightSum += w;
            }
            if (weightSum == 0)
                continue; // unreachable in practice: Dijkstra settles a predecessor first

            uv[v] = uvSum / weightSum;

            // Re-orthonormalize the averaged transported axis against the vertex normal.
            var nv = normals[v];
            if (nv == Vector3d.Zero)
                nv = seedNormal; // degenerate ring: fall back to the seed's plane
            var tangent = t1Sum - nv * t1Sum.Dot(nv);
            if (!tangent.TryNormalize(Tolerance.Default, out var t1v))
                t1v = nv.ArbitraryPerpendicular(Tolerance.Default);
            t1[v] = t1v;
            t2[v] = nv.Cross(t1v);
            hasUv[v] = true;
            mapped.Add(v);
        }

        return new MeshLocalParam(mesh, seedVertex, maxRadius, uv, hasUv, mapped);
    }

    /// <summary>
    /// Rotates <paramref name="vector"/> by the minimal rotation taking unit vector
    /// <paramref name="from"/> to unit vector <paramref name="to"/> — the trig-free
    /// Rodrigues form v·c + (axis × v) + axis·(axis·v)/(1 + c), exact and stable for
    /// every c &gt; −1 because (1 − c)/sin² = 1/(1 + c). Antiparallel normals (c near
    /// −1, a fold within one edge) have no well-defined minimal rotation; the vector is
    /// projected into the target plane instead.
    /// </summary>
    private static Vector3d RotateBetween(in Vector3d from, in Vector3d to, in Vector3d vector)
    {
        double c = from.Dot(to);
        if (c <= -1 + 1e-12)
        {
            // Semantic guard, not a length tolerance: both inputs are unit vectors, so
            // c is a pure cosine and the branch only exists to avoid dividing by 1 + c.
            return vector - to * vector.Dot(to);
        }
        var axis = from.Cross(to);
        return vector * c + axis.Cross(vector) + axis * (axis.Dot(vector) / (1 + c));
    }
}
