using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Half-edge (doubly connected edge list) polygon mesh. Every edge is a pair of opposite
/// half-edges; boundary edges get an explicit half-edge with <c>face = -1</c> whose Next
/// links trace the boundary loop, so <c>Twin</c> always exists and traversal never
/// branches. Faces are arbitrary planar polygons (n ≥ 3). The structure is manifold by
/// construction: <see cref="Build"/> rejects non-manifold edges, inconsistent winding,
/// and non-manifold (bow-tie) boundary vertices.
/// Topology is immutable after <see cref="Build"/>; algorithms produce new meshes.
/// </summary>
public sealed class HalfEdgeMesh
{
    // Struct-of-arrays storage; handles (Vertex/HalfEdge/Face) are thin index wrappers.
    private readonly List<Vector3d> _positions = [];
    private readonly List<int> _vertexOut = [];   // one outgoing half-edge per vertex, -1 if isolated
    private readonly List<int> _heOrigin = [];
    private readonly List<int> _heNext = [];
    private readonly List<int> _hePrev = [];
    private readonly List<int> _heTwin = [];
    private readonly List<int> _heFace = [];      // -1 for boundary half-edges
    private readonly List<int> _faceHe = [];

    private HalfEdgeMesh()
    {
    }

    public int VertexCount => _positions.Count;
    public int HalfEdgeCount => _heOrigin.Count;
    public int EdgeCount => _heOrigin.Count / 2;
    public int FaceCount => _faceHe.Count;

    public IEnumerable<Vertex> Vertices => Enumerable.Range(0, VertexCount).Select(i => new Vertex(this, i));
    public IEnumerable<HalfEdge> HalfEdges => Enumerable.Range(0, HalfEdgeCount).Select(i => new HalfEdge(this, i));
    public IEnumerable<Face> Faces => Enumerable.Range(0, FaceCount).Select(i => new Face(this, i));

    /// <summary>One half-edge per undirected edge (the one with the lower index of the pair).</summary>
    public IEnumerable<HalfEdge> Edges => HalfEdges.Where(h => h.Index < h.Twin.Index);

    public Vertex GetVertex(int index) => new(this, CheckIndex(index, VertexCount));
    public HalfEdge GetHalfEdge(int index) => new(this, CheckIndex(index, HalfEdgeCount));
    public Face GetFace(int index) => new(this, CheckIndex(index, FaceCount));

    public Vector3d GetPosition(int vertex) => _positions[vertex];

    private static int CheckIndex(int index, int count) =>
        index >= 0 && index < count ? index : throw new ArgumentOutOfRangeException(nameof(index));

    internal int HeOrigin(int he) => _heOrigin[he];
    internal int HeNext(int he) => _heNext[he];
    internal int HePrev(int he) => _hePrev[he];
    internal int HeTwin(int he) => _heTwin[he];
    internal int HeFace(int he) => _heFace[he];
    internal int FaceAnyHalfEdge(int face) => _faceHe[face];
    internal int VertexOutgoing(int vertex) => _vertexOut[vertex];

    /// <summary>
    /// Builds a mesh from shared vertex positions and per-face vertex index loops
    /// (counter-clockwise when viewed from outside). Throws <see cref="ArgumentException"/>
    /// on degenerate faces, repeated directed edges (non-manifold or inconsistent winding),
    /// edges shared by more than two faces, or non-manifold boundary vertices.
    /// </summary>
    public static HalfEdgeMesh Build(IReadOnlyList<Vector3d> positions, IEnumerable<IReadOnlyList<int>> faces)
    {
        var mesh = new HalfEdgeMesh();
        mesh._positions.AddRange(positions);
        for (int i = 0; i < positions.Count; i++)
            mesh._vertexOut.Add(-1);

        var directed = new Dictionary<(int From, int To), int>();

        foreach (var face in faces)
        {
            int n = face.Count;
            if (n < 3)
                throw new ArgumentException($"Face has {n} vertices; at least 3 required.");

            int faceIndex = mesh._faceHe.Count;
            int start = mesh.HalfEdgeCount;
            mesh._faceHe.Add(start);

            for (int i = 0; i < n; i++)
            {
                int v = face[i];
                if (v < 0 || v >= positions.Count)
                    throw new ArgumentException($"Face references vertex {v}, but only {positions.Count} positions were given.");
                mesh._heOrigin.Add(v);
                mesh._heNext.Add(start + (i + 1) % n);
                mesh._hePrev.Add(start + (i - 1 + n) % n);
                mesh._heTwin.Add(-1);
                mesh._heFace.Add(faceIndex);
                if (mesh._vertexOut[v] < 0)
                    mesh._vertexOut[v] = start + i;
            }

            for (int i = 0; i < n; i++)
            {
                int from = face[i];
                int to = face[(i + 1) % n];
                if (from == to)
                    throw new ArgumentException($"Face {faceIndex} contains a degenerate edge ({from} → {to}).");
                if (!directed.TryAdd((from, to), start + i))
                    throw new ArgumentException(
                        $"Directed edge {from} → {to} appears twice: mesh is non-manifold or a face is wound inconsistently.");
                if (directed.TryGetValue((to, from), out int twin))
                {
                    mesh._heTwin[start + i] = twin;
                    mesh._heTwin[twin] = start + i;
                }
            }
        }

        mesh.CreateBoundaryHalfEdges();
        return mesh;
    }

    private void CreateBoundaryHalfEdges()
    {
        int interiorCount = HalfEdgeCount;
        var boundaryByOrigin = new Dictionary<int, int>();

        for (int he = 0; he < interiorCount; he++)
        {
            if (_heTwin[he] >= 0)
                continue;

            int b = HalfEdgeCount;
            int origin = _heOrigin[_heNext[he]]; // destination of the interior half-edge
            _heOrigin.Add(origin);
            _heNext.Add(-1);
            _hePrev.Add(-1);
            _heTwin.Add(he);
            _heFace.Add(-1);
            _heTwin[he] = b;

            if (!boundaryByOrigin.TryAdd(origin, b))
                throw new ArgumentException(
                    $"Vertex {origin} lies on more than one boundary edge fan: non-manifold (bow-tie) vertex.");

            // Prefer a boundary outgoing half-edge so boundary vertices are easy to detect.
            _vertexOut[origin] = b;
        }

        // Chain boundary half-edges into loops: the successor starts at this one's destination.
        for (int b = interiorCount; b < HalfEdgeCount; b++)
        {
            int destination = _heOrigin[_heTwin[b]];
            int next = boundaryByOrigin[destination];
            _heNext[b] = next;
            _hePrev[next] = b;
        }
    }

    /// <summary>Checks structural invariants; throws <see cref="InvalidOperationException"/> with details on the first violation.</summary>
    public void Validate()
    {
        for (int he = 0; he < HalfEdgeCount; he++)
        {
            if (_heTwin[he] < 0 || _heTwin[_heTwin[he]] != he)
                throw new InvalidOperationException($"Half-edge {he}: twin is not an involution.");
            if (_heTwin[he] == he)
                throw new InvalidOperationException($"Half-edge {he} is its own twin.");
            if (_heNext[he] < 0 || _hePrev[_heNext[he]] != he)
                throw new InvalidOperationException($"Half-edge {he}: prev(next) does not return to it.");
            if (_heFace[_heNext[he]] != _heFace[he])
                throw new InvalidOperationException($"Half-edge {he}: next crosses into a different face.");
            if (_heOrigin[_heNext[he]] != _heOrigin[_heTwin[he]])
                throw new InvalidOperationException($"Half-edge {he}: destination disagrees between next and twin.");
        }

        for (int f = 0; f < FaceCount; f++)
        {
            int start = _faceHe[f];
            int he = start;
            for (int steps = 0; ; steps++)
            {
                if (steps > HalfEdgeCount)
                    throw new InvalidOperationException($"Face {f}: next-cycle does not close.");
                if (_heFace[he] != f)
                    throw new InvalidOperationException($"Face {f}: contains half-edge {he} labeled with face {_heFace[he]}.");
                he = _heNext[he];
                if (he == start)
                    break;
            }
        }

        for (int v = 0; v < VertexCount; v++)
        {
            int start = _vertexOut[v];
            if (start < 0)
                continue;
            int he = start;
            for (int steps = 0; ; steps++)
            {
                if (steps > HalfEdgeCount)
                    throw new InvalidOperationException($"Vertex {v}: outgoing-cycle does not close.");
                if (_heOrigin[he] != v)
                    throw new InvalidOperationException($"Vertex {v}: outgoing walk reached half-edge {he} with origin {_heOrigin[he]}.");
                he = _heTwin[_hePrev[he]];
                if (he == start)
                    break;
            }
        }
    }

    /// <summary>True when the mesh has no boundary (every half-edge borders a face).</summary>
    public bool IsClosed
    {
        get
        {
            for (int he = 0; he < HalfEdgeCount; he++)
            {
                if (_heFace[he] < 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>V − E + F. 2 for a closed mesh of genus 0.</summary>
    public int EulerCharacteristic => VertexCount - EdgeCount + FaceCount;

    public Aabb ComputeBounds()
    {
        var box = Aabb.Empty;
        foreach (var p in _positions)
            box = box.Union(p);
        return box;
    }

    public double SurfaceArea()
    {
        double area = 0;
        for (int f = 0; f < FaceCount; f++)
            area += FaceNormalRaw(f).Length;
        return area;
    }

    /// <summary>
    /// Signed enclosed volume via the divergence theorem; positive for outward
    /// counter-clockwise winding. Requires a closed mesh.
    /// </summary>
    public double Volume()
    {
        if (!IsClosed)
            throw new InvalidOperationException("Volume is only defined for a closed mesh.");

        double volume = 0;
        for (int f = 0; f < FaceCount; f++)
        {
            int start = _faceHe[f];
            var p0 = _positions[_heOrigin[start]];
            int he = _heNext[start];
            while (_heNext[he] != start)
            {
                var p1 = _positions[_heOrigin[he]];
                var p2 = _positions[_heOrigin[_heNext[he]]];
                volume += p0.Dot(p1.Cross(p2));
                he = _heNext[he];
            }
        }
        return volume / 6.0;
    }

    /// <summary>Face normal with magnitude equal to the face area (Newell's method), unnormalized.</summary>
    internal Vector3d FaceNormalRaw(int face)
    {
        double nx = 0, ny = 0, nz = 0;
        int start = _faceHe[face];
        int he = start;
        do
        {
            var p = _positions[_heOrigin[he]];
            var q = _positions[_heOrigin[_heNext[he]]];
            nx += (p.Y - q.Y) * (p.Z + q.Z);
            ny += (p.Z - q.Z) * (p.X + q.X);
            nz += (p.X - q.X) * (p.Y + q.Y);
            he = _heNext[he];
        } while (he != start);
        return new Vector3d(nx * 0.5, ny * 0.5, nz * 0.5);
    }

    /// <summary>Area-weighted vertex normals for the whole mesh; zero for isolated vertices or degenerate fans.</summary>
    public Vector3d[] ComputeVertexNormals()
    {
        var faceNormals = new Vector3d[FaceCount];
        for (int f = 0; f < FaceCount; f++)
            faceNormals[f] = FaceNormalRaw(f);

        var normals = new Vector3d[VertexCount];
        for (int he = 0; he < HalfEdgeCount; he++)
        {
            int f = _heFace[he];
            if (f >= 0)
                normals[_heOrigin[he]] += faceNormals[f];
        }
        for (int v = 0; v < VertexCount; v++)
        {
            if (!normals[v].TryNormalize(Tolerance.Default, out var n))
                n = Vector3d.Zero;
            normals[v] = n;
        }
        return normals;
    }

    /// <summary>Boundary loops as chains of boundary half-edges (face = -1), each in walk order.</summary>
    public List<List<HalfEdge>> BoundaryLoops()
    {
        var loops = new List<List<HalfEdge>>();
        var visited = new bool[HalfEdgeCount];
        for (int he = 0; he < HalfEdgeCount; he++)
        {
            if (_heFace[he] >= 0 || visited[he])
                continue;
            var loop = new List<HalfEdge>();
            int e = he;
            do
            {
                visited[e] = true;
                loop.Add(new HalfEdge(this, e));
                e = _heNext[e];
            } while (e != he);
            loops.Add(loop);
        }
        return loops;
    }

    /// <summary>Round-trips back to shared positions + per-face index loops.</summary>
    public (Vector3d[] Positions, List<int[]> Faces) ToIndexed()
    {
        var faces = new List<int[]>(FaceCount);
        for (int f = 0; f < FaceCount; f++)
        {
            var loop = new List<int>();
            int start = _faceHe[f];
            int he = start;
            do
            {
                loop.Add(_heOrigin[he]);
                he = _heNext[he];
            } while (he != start);
            faces.Add([.. loop]);
        }
        return ([.. _positions], faces);
    }

    /// <summary>New mesh with every face fan-triangulated from its first vertex.</summary>
    public HalfEdgeMesh Triangulated()
    {
        var (positions, faces) = ToIndexed();
        var triangles = new List<int[]>();
        foreach (var face in faces)
        {
            for (int i = 1; i < face.Length - 1; i++)
                triangles.Add([face[0], face[i], face[i + 1]]);
        }
        return Build(positions, triangles);
    }
}
