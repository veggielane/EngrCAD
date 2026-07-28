using System.Runtime.InteropServices;
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
        ArgumentNullException.ThrowIfNull(faces);
        var corners = new List<int>();
        var faceStarts = new List<int> { 0 };
        foreach (var face in faces)
        {
            for (int i = 0; i < face.Count; i++)
                corners.Add(face[i]);
            faceStarts.Add(corners.Count);
        }
        return Build(positions, CollectionsMarshal.AsSpan(corners), CollectionsMarshal.AsSpan(faceStarts));
    }

    /// <summary>
    /// Builds a mesh from vertex positions and face loops packed into ONE index buffer:
    /// face <c>f</c> owns <c>corners[faceStarts[f] .. faceStarts[f + 1])</c>, so
    /// <paramref name="faceStarts"/> has one more entry than there are faces and its last
    /// entry is <c>corners.Length</c>. Same validation and same result as the
    /// loop-per-face overload; this shape exists for generators that already know their
    /// counts (grid polygonizers, tessellators) and would otherwise allocate one array
    /// per face.
    /// </summary>
    public static HalfEdgeMesh Build(
        IReadOnlyList<Vector3d> positions, ReadOnlySpan<int> corners, ReadOnlySpan<int> faceStarts)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (faceStarts.Length < 1 || faceStarts[0] != 0 || faceStarts[^1] != corners.Length)
            throw new ArgumentException(
                $"faceStarts must run from 0 to corners.Length ({corners.Length}); " +
                $"got {faceStarts.Length} entries {(faceStarts.Length > 0 ? $"[{faceStarts[0]}..{faceStarts[^1]}]" : "")}.",
                nameof(faceStarts));
        return BuildCore(positions, corners, faceStarts);
    }

    /// <summary>
    /// <see cref="Build(IReadOnlyList{Vector3d}, ReadOnlySpan{int}, ReadOnlySpan{int})"/>
    /// for a buffer whose faces all have the same degree — the quad grids every
    /// polygonizer emits.
    /// </summary>
    public static HalfEdgeMesh Build(
        IReadOnlyList<Vector3d> positions, ReadOnlySpan<int> corners, int verticesPerFace)
    {
        if (verticesPerFace < 3)
            throw new ArgumentOutOfRangeException(nameof(verticesPerFace), verticesPerFace, "At least 3 required.");
        if (corners.Length % verticesPerFace != 0)
            throw new ArgumentException(
                $"{corners.Length} indices is not a whole number of {verticesPerFace}-sided faces.", nameof(corners));
        int faceCount = corners.Length / verticesPerFace;
        var faceStarts = new int[faceCount + 1];
        for (int f = 0; f <= faceCount; f++)
            faceStarts[f] = f * verticesPerFace;
        return BuildCore(positions, corners, faceStarts);
    }

    /// <summary>
    /// The one construction path. Origin/next/prev/face fall straight out of the face
    /// loops; the only real work is pairing each directed edge with its reverse.
    /// <para>
    /// That pairing is a <b>counting sort over the edges' lower endpoint</b>, not a hash
    /// table. Every directed edge is filed under <c>min(from, to)</c>, so an edge and its
    /// reverse always land in the same bucket, and a bucket holds only the edges of one
    /// vertex's fan — three to six entries on any real mesh. One linear scan of that
    /// bucket then answers both questions at once: an earlier entry with the SAME
    /// direction is the non-manifold duplicate, an earlier entry with the opposite
    /// direction is the twin. Buckets are filled in ascending half-edge order, so "earlier"
    /// is a <c>break</c> rather than a comparison, and the scan sees exactly the entries
    /// the old <c>Dictionary&lt;(int, int), int&gt;</c> probe would have — including the
    /// order in which errors are detected, which is what keeps the exception messages
    /// identical.
    /// </para>
    /// </summary>
    private static HalfEdgeMesh BuildCore(
        IReadOnlyList<Vector3d> positions, ReadOnlySpan<int> corners, ReadOnlySpan<int> faceStarts)
    {
        int vertexCount = positions.Count;
        int faceCount = faceStarts.Length - 1;
        int interiorCount = corners.Length;

        var mesh = new HalfEdgeMesh();
        mesh._positions.AddRange(positions);
        CollectionsMarshal.SetCount(mesh._vertexOut, vertexCount);
        CollectionsMarshal.SetCount(mesh._heOrigin, interiorCount);
        CollectionsMarshal.SetCount(mesh._heNext, interiorCount);
        CollectionsMarshal.SetCount(mesh._hePrev, interiorCount);
        CollectionsMarshal.SetCount(mesh._heTwin, interiorCount);
        CollectionsMarshal.SetCount(mesh._heFace, interiorCount);
        CollectionsMarshal.SetCount(mesh._faceHe, faceCount);
        var vertexOut = CollectionsMarshal.AsSpan(mesh._vertexOut);
        var heOrigin = CollectionsMarshal.AsSpan(mesh._heOrigin);
        var heNext = CollectionsMarshal.AsSpan(mesh._heNext);
        var hePrev = CollectionsMarshal.AsSpan(mesh._hePrev);
        var heTwin = CollectionsMarshal.AsSpan(mesh._heTwin);
        var heFace = CollectionsMarshal.AsSpan(mesh._heFace);
        var faceHe = CollectionsMarshal.AsSpan(mesh._faceHe);
        vertexOut.Fill(-1);
        heTwin.Fill(-1);

        // Bucket every directed edge under its lower endpoint. Out-of-range indices are
        // skipped here rather than reported: the per-face loop below reaches them in the
        // caller's own order and throws there, so error reporting stays where it was.
        var bucketStart = new int[vertexCount + 1];
        for (int f = 0; f < faceCount; f++)
        {
            int start = faceStarts[f], n = faceStarts[f + 1] - start;
            for (int i = 0; i < n; i++)
            {
                int from = corners[start + i], to = corners[start + (i + 1) % n];
                if ((uint)from < (uint)vertexCount && (uint)to < (uint)vertexCount)
                    bucketStart[Math.Min(from, to) + 1]++;
            }
        }
        for (int v = 0; v < vertexCount; v++)
            bucketStart[v + 1] += bucketStart[v];
        var bucketEntry = new int[vertexCount == 0 ? 0 : bucketStart[vertexCount]];
        var bucketCursor = new int[Math.Max(1, vertexCount)];
        bucketStart.AsSpan(0, vertexCount).CopyTo(bucketCursor);
        for (int f = 0; f < faceCount; f++)
        {
            int start = faceStarts[f], n = faceStarts[f + 1] - start;
            for (int i = 0; i < n; i++)
            {
                int from = corners[start + i], to = corners[start + (i + 1) % n];
                if ((uint)from < (uint)vertexCount && (uint)to < (uint)vertexCount)
                    bucketEntry[bucketCursor[Math.Min(from, to)]++] = start + i;
            }
        }

        for (int f = 0; f < faceCount; f++)
        {
            int start = faceStarts[f], n = faceStarts[f + 1] - start;
            if (n < 3)
                throw new ArgumentException($"Face has {n} vertices; at least 3 required.");
            faceHe[f] = start;

            for (int i = 0; i < n; i++)
            {
                int v = corners[start + i];
                if (v < 0 || v >= vertexCount)
                    throw new ArgumentException($"Face references vertex {v}, but only {vertexCount} positions were given.");
                heOrigin[start + i] = v;
                heNext[start + i] = start + (i + 1) % n;
                hePrev[start + i] = start + (i - 1 + n) % n;
                heFace[start + i] = f;
                if (vertexOut[v] < 0)
                    vertexOut[v] = start + i;
            }

            for (int i = 0; i < n; i++)
            {
                int he = start + i;
                int from = corners[he];
                int to = corners[start + (i + 1) % n];
                if (from == to)
                    throw new ArgumentException($"Face {f} contains a degenerate edge ({from} → {to}).");

                // One pass over the shared bucket answers both questions. Entries are in
                // ascending half-edge order, so everything past `he` is a face this loop
                // has not ingested yet and cannot have paired with.
                int bucket = Math.Min(from, to);
                for (int b = bucketStart[bucket]; b < bucketStart[bucket + 1]; b++)
                {
                    int other = bucketEntry[b];
                    if (other >= he)
                        break;
                    int otherFrom = heOrigin[other];
                    int otherTo = heOrigin[heNext[other]];
                    if (otherFrom == from && otherTo == to)
                        throw new ArgumentException(
                            $"Directed edge {from} → {to} appears twice: mesh is non-manifold or a face is wound inconsistently.");
                    if (otherFrom == to && otherTo == from && heTwin[other] < 0)
                    {
                        heTwin[he] = other;
                        heTwin[other] = he;
                    }
                }
            }
        }

        mesh.CreateBoundaryHalfEdges();
        return mesh;
    }

    private void CreateBoundaryHalfEdges()
    {
        int interiorCount = HalfEdgeCount;
        // One slot per vertex instead of a dictionary — a boundary vertex has exactly one
        // outgoing boundary half-edge, and a second one IS the bow-tie. Allocated lazily
        // because a closed mesh never reaches it.
        int[]? boundaryByOrigin = null;

        for (int he = 0; he < interiorCount; he++)
        {
            if (_heTwin[he] >= 0)
                continue;

            if (boundaryByOrigin is null)
            {
                boundaryByOrigin = new int[VertexCount];
                Array.Fill(boundaryByOrigin, -1);
            }

            int b = HalfEdgeCount;
            int origin = _heOrigin[_heNext[he]]; // destination of the interior half-edge
            _heOrigin.Add(origin);
            _heNext.Add(-1);
            _hePrev.Add(-1);
            _heTwin.Add(he);
            _heFace.Add(-1);
            _heTwin[he] = b;

            if (boundaryByOrigin[origin] >= 0)
                throw new ArgumentException(
                    $"Vertex {origin} lies on more than one boundary edge fan: non-manifold (bow-tie) vertex.");
            boundaryByOrigin[origin] = b;

            // Prefer a boundary outgoing half-edge so boundary vertices are easy to detect.
            _vertexOut[origin] = b;
        }

        if (boundaryByOrigin is null)
            return;

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
    /// counter-clockwise winding. Requires a topologically closed mesh.
    /// </summary>
    public double Volume()
    {
        if (!IsClosed)
            throw new InvalidOperationException("Volume is only defined for a closed mesh.");
        return SignedVolume();
    }

    /// <summary>
    /// Signed volume without the closed-topology check. Meaningful whenever the surface is
    /// geometrically watertight — e.g. boolean results whose seams carry T-junctions.
    /// </summary>
    public double SignedVolume()
    {
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

    /// <summary>
    /// New mesh with every position mapped through <paramref name="transform"/>.
    /// Negative-determinant maps (mirrors) reverse face winding so the result stays
    /// outward-oriented (closed solids keep positive volume).
    /// </summary>
    public HalfEdgeMesh Transformed(in Matrix4d transform)
    {
        var (positions, faces) = ToIndexed();
        for (int i = 0; i < positions.Length; i++)
            positions[i] = transform.TransformPoint(positions[i]);
        IEnumerable<IReadOnlyList<int>> orderedFaces = transform.Determinant < 0
            ? faces.Select(f => (IReadOnlyList<int>)[.. f.Reverse()])
            : faces;
        return Build(positions, orderedFaces);
    }

    /// <summary>True when every face already has exactly three sides.</summary>
    public bool IsTriangulated
    {
        get
        {
            for (int f = 0; f < FaceCount; f++)
            {
                int start = _faceHe[f];
                if (_heNext[_heNext[_heNext[start]]] != start)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// New mesh with every face fan-triangulated from its first vertex — or this mesh
    /// itself when it is already all triangles, which is safe because a
    /// <see cref="HalfEdgeMesh"/> is immutable once built.
    /// <para>
    /// The short circuit matters: the general path is a full <see cref="Build"/>, manifold
    /// validation included, and the exact boolean triangulates BOTH operands on entry. A
    /// boolean's output is all triangles, so cascaded operations (drilling five holes) were
    /// paying for a complete revalidating rebuild of both operands at every stage.
    /// </para>
    /// </summary>
    public HalfEdgeMesh Triangulated()
    {
        if (IsTriangulated)
            return this;
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
