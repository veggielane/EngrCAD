using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Lightweight handle to a vertex of a <see cref="HalfEdgeMesh"/>. Handles are cheap
/// value types designed for fluent LINQ traversal, e.g.
/// <c>vertex.OutgoingHalfEdges().Where(h => !h.IsBoundary).Select(h => h.Face)</c>.
/// </summary>
public readonly struct Vertex : IEquatable<Vertex>
{
    public HalfEdgeMesh Mesh { get; }
    public int Index { get; }

    internal Vertex(HalfEdgeMesh mesh, int index)
    {
        Mesh = mesh;
        Index = index;
    }

    public Vector3d Position => Mesh.GetPosition(Index);

    public bool IsIsolated => Mesh.VertexOutgoing(Index) < 0;

    /// <summary>An arbitrary outgoing half-edge (a boundary one, if the vertex lies on a boundary).</summary>
    public HalfEdge Outgoing => IsIsolated
        ? throw new InvalidOperationException($"Vertex {Index} is isolated.")
        : new HalfEdge(Mesh, Mesh.VertexOutgoing(Index));

    public bool IsBoundary => !IsIsolated && Outgoing.IsBoundary;

    public int Valence => IsIsolated ? 0 : OutgoingHalfEdges().Count();

    /// <summary>All half-edges leaving this vertex, one full turn around it.</summary>
    public IEnumerable<HalfEdge> OutgoingHalfEdges()
    {
        if (IsIsolated)
            yield break;
        int start = Mesh.VertexOutgoing(Index);
        int he = start;
        int guard = Mesh.HalfEdgeCount;
        do
        {
            yield return new HalfEdge(Mesh, he);
            he = Mesh.HeTwin(Mesh.HePrev(he));
            if (guard-- < 0)
                throw new InvalidOperationException($"Vertex {Index}: outgoing walk did not close (corrupt topology).");
        } while (he != start);
    }

    public IEnumerable<Vertex> Neighbors() => OutgoingHalfEdges().Select(h => h.Destination);

    /// <summary>Incident real faces (boundary "faces" are skipped).</summary>
    public IEnumerable<Face> IncidentFaces() =>
        OutgoingHalfEdges().Where(h => !h.IsBoundary).Select(h => h.Face);

    /// <summary>Area-weighted normal of the incident faces; zero if degenerate.</summary>
    public Vector3d Normal()
    {
        var sum = Vector3d.Zero;
        foreach (var face in IncidentFaces())
            sum += face.NormalRaw;
        return sum.TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.Zero;
    }

    public bool Equals(Vertex other) => ReferenceEquals(Mesh, other.Mesh) && Index == other.Index;
    public override bool Equals(object? obj) => obj is Vertex v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Mesh, Index);
    public static bool operator ==(Vertex a, Vertex b) => a.Equals(b);
    public static bool operator !=(Vertex a, Vertex b) => !a.Equals(b);
    public override string ToString() => $"v{Index} @ {Position}";
}

/// <summary>
/// Handle to a directed half-edge. Boundary half-edges (<see cref="IsBoundary"/>) carry
/// no face and chain along the mesh boundary.
/// </summary>
public readonly struct HalfEdge : IEquatable<HalfEdge>
{
    public HalfEdgeMesh Mesh { get; }
    public int Index { get; }

    internal HalfEdge(HalfEdgeMesh mesh, int index)
    {
        Mesh = mesh;
        Index = index;
    }

    public Vertex Origin => new(Mesh, Mesh.HeOrigin(Index));
    public Vertex Destination => new(Mesh, Mesh.HeOrigin(Mesh.HeTwin(Index)));
    public HalfEdge Next => new(Mesh, Mesh.HeNext(Index));
    public HalfEdge Prev => new(Mesh, Mesh.HePrev(Index));
    public HalfEdge Twin => new(Mesh, Mesh.HeTwin(Index));

    /// <summary>True when this half-edge borders the void (no face on its left).</summary>
    public bool IsBoundary => Mesh.HeFace(Index) < 0;

    /// <summary>True when the undirected edge lies on the mesh boundary.</summary>
    public bool IsBoundaryEdge => IsBoundary || Twin.IsBoundary;

    public Face Face => Mesh.HeFace(Index) is int f && f >= 0
        ? new Face(Mesh, f)
        : throw new InvalidOperationException($"Half-edge {Index} is a boundary half-edge and has no face.");

    public Vector3d Vector => Destination.Position - Origin.Position;
    public double Length => Vector.Length;

    /// <summary>Interior angle between the two faces at this edge, in [0, π]; π for flat. Boundary edges throw.</summary>
    public double DihedralAngle()
    {
        if (IsBoundaryEdge)
            throw new InvalidOperationException("Dihedral angle requires faces on both sides.");
        var n1 = Face.Normal();
        var n2 = Twin.Face.Normal();
        return Math.PI - n1.AngleTo(n2);
    }

    public bool Equals(HalfEdge other) => ReferenceEquals(Mesh, other.Mesh) && Index == other.Index;
    public override bool Equals(object? obj) => obj is HalfEdge h && Equals(h);
    public override int GetHashCode() => HashCode.Combine(Mesh, Index);
    public static bool operator ==(HalfEdge a, HalfEdge b) => a.Equals(b);
    public static bool operator !=(HalfEdge a, HalfEdge b) => !a.Equals(b);
    public override string ToString() => $"he{Index}: v{Mesh.HeOrigin(Index)} → v{Mesh.HeOrigin(Mesh.HeTwin(Index))}";
}

/// <summary>Handle to a polygon face.</summary>
public readonly struct Face : IEquatable<Face>
{
    public HalfEdgeMesh Mesh { get; }
    public int Index { get; }

    internal Face(HalfEdgeMesh mesh, int index)
    {
        Mesh = mesh;
        Index = index;
    }

    /// <summary>An arbitrary half-edge of this face's loop.</summary>
    public HalfEdge AnyHalfEdge => new(Mesh, Mesh.FaceAnyHalfEdge(Index));

    public IEnumerable<HalfEdge> HalfEdges()
    {
        int start = Mesh.FaceAnyHalfEdge(Index);
        int he = start;
        int guard = Mesh.HalfEdgeCount;
        do
        {
            yield return new HalfEdge(Mesh, he);
            he = Mesh.HeNext(he);
            if (guard-- < 0)
                throw new InvalidOperationException($"Face {Index}: loop did not close (corrupt topology).");
        } while (he != start);
    }

    public IEnumerable<Vertex> Vertices() => HalfEdges().Select(h => h.Origin);

    public IEnumerable<Face> AdjacentFaces() =>
        HalfEdges().Where(h => !h.Twin.IsBoundary).Select(h => h.Twin.Face);

    public int Degree => HalfEdges().Count();

    /// <summary>Axis-aligned bounds of the face's vertices.</summary>
    public Aabb Bounds
    {
        get
        {
            var box = Aabb.Empty;
            foreach (var v in Vertices())
                box = box.Union(v.Position);
            return box;
        }
    }

    /// <summary>Unnormalized Newell normal; its magnitude is the face area.</summary>
    public Vector3d NormalRaw => Mesh.FaceNormalRaw(Index);

    /// <summary>Unit normal; zero vector for degenerate faces.</summary>
    public Vector3d Normal() => NormalRaw.TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.Zero;

    public double Area => NormalRaw.Length;

    public Vector3d Centroid()
    {
        var sum = Vector3d.Zero;
        int count = 0;
        foreach (var v in Vertices())
        {
            sum += v.Position;
            count++;
        }
        return sum / count;
    }

    public bool Equals(Face other) => ReferenceEquals(Mesh, other.Mesh) && Index == other.Index;
    public override bool Equals(object? obj) => obj is Face f && Equals(f);
    public override int GetHashCode() => HashCode.Combine(Mesh, Index);
    public static bool operator ==(Face a, Face b) => a.Equals(b);
    public static bool operator !=(Face a, Face b) => !a.Equals(b);
    public override string ToString() => $"f{Index} ({Degree}-gon)";
}
