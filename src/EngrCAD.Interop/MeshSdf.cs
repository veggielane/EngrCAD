using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>How <see cref="MeshSdf"/> decides which side of the surface a point is on.</summary>
public enum MeshSignSource
{
    /// <summary>
    /// Angle-weighted pseudonormal of the closest feature (Bærentzen &amp; Aanæs) — exact
    /// for watertight meshes, requires a closed mesh. The default.
    /// </summary>
    Pseudonormal,

    /// <summary>
    /// Fast generalized winding number (<see cref="MeshWindingNumber"/>, Barill et al.
    /// 2018): inside where the winding number exceeds ½. Also works for open
    /// (non-watertight) meshes, where the distance is to the existing surface and the
    /// sign degrades gracefully near holes.
    /// </summary>
    WindingNumber,
}

/// <summary>
/// Signed distance field of a triangle mesh, completing the mesh → implicit direction of
/// the interop triangle: mesh geometry becomes a first-class <see cref="Sdf"/> node
/// composable with analytic primitives and blends.
/// Distance queries walk a BVH over the triangles (branch and bound); the sign comes from
/// the angle-weighted pseudonormal of the closest feature (face, edge, or vertex) —
/// Bærentzen &amp; Aanæs' criterion, which is exact for watertight meshes even when the
/// closest feature is an edge or vertex — or, opt-in via
/// <see cref="MeshSignSource.WindingNumber"/>, from the fast generalized winding number,
/// which additionally tolerates open meshes.
/// </summary>
public sealed class MeshSdf : Sdf
{
    // Per-triangle corner positions and vertex ids.
    private readonly Vector3d[] _cornerA;
    private readonly Vector3d[] _cornerB;
    private readonly Vector3d[] _cornerC;
    private readonly int[]? _vertexIds;          // 3 per triangle (pseudonormal mode)
    private readonly Vector3d[]? _faceNormals;
    private readonly Vector3d[]? _edgeNormals;   // 3 per triangle: edges (a,b), (b,c), (c,a)
    private readonly Vector3d[]? _vertexNormals; // per mesh vertex, angle-weighted
    private readonly MeshWindingNumber? _winding; // winding-number mode
    private readonly Bvh _bvh;
    private readonly Aabb _bounds;

    public MeshSdf(HalfEdgeMesh mesh) : this(mesh, MeshSignSource.Pseudonormal)
    {
    }

    public MeshSdf(HalfEdgeMesh mesh, MeshSignSource signSource)
    {
        if (signSource == MeshSignSource.Pseudonormal && !mesh.IsClosed)
            throw new ArgumentException("A signed distance field requires a closed mesh.", nameof(mesh));

        var triangulated = mesh.Triangulated();
        int faceCount = triangulated.FaceCount;
        _cornerA = new Vector3d[faceCount];
        _cornerB = new Vector3d[faceCount];
        _cornerC = new Vector3d[faceCount];
        var boxes = new Aabb[faceCount];

        bool pseudonormal = signSource == MeshSignSource.Pseudonormal;
        if (pseudonormal)
        {
            _vertexIds = new int[faceCount * 3];
            _faceNormals = new Vector3d[faceCount];
            _edgeNormals = new Vector3d[faceCount * 3];
            _vertexNormals = new Vector3d[triangulated.VertexCount];
        }
        else
        {
            // Hand over the already-triangulated mesh so MeshWindingNumber does not
            // triangulate a second time.
            _winding = MeshWindingNumber.FromTriangulated(triangulated);
        }

        foreach (var face in triangulated.Faces)
        {
            int i = face.Index;
            var h0 = face.AnyHalfEdge;
            var h1 = h0.Next;
            var h2 = h1.Next;
            var a = h0.Origin.Position;
            var b = h1.Origin.Position;
            var c = h2.Origin.Position;
            _cornerA[i] = a;
            _cornerB[i] = b;
            _cornerC[i] = c;
            boxes[i] = Aabb.FromPoints([a, b, c]);

            if (!pseudonormal)
                continue;
            _vertexIds![i * 3] = h0.Origin.Index;
            _vertexIds[i * 3 + 1] = h1.Origin.Index;
            _vertexIds[i * 3 + 2] = h2.Origin.Index;
            _faceNormals![i] = face.Normal();

            // Angle-weighted accumulation for vertex pseudonormals.
            _vertexNormals![h0.Origin.Index] += _faceNormals[i] * (b - a).AngleTo(c - a);
            _vertexNormals[h1.Origin.Index] += _faceNormals[i] * (c - b).AngleTo(a - b);
            _vertexNormals[h2.Origin.Index] += _faceNormals[i] * (a - c).AngleTo(b - c);
        }

        if (pseudonormal)
        {
            // Edge pseudonormals: mean of the two adjacent face normals.
            foreach (var face in triangulated.Faces)
            {
                int i = face.Index;
                var he = face.AnyHalfEdge;
                for (int k = 0; k < 3; k++)
                {
                    var sum = _faceNormals![i] + _faceNormals[he.Twin.Face.Index];
                    _edgeNormals![i * 3 + k] = sum.TryNormalize(Tolerance.Default, out var n) ? n : _faceNormals[i];
                    he = he.Next;
                }
            }
            for (int v = 0; v < _vertexNormals!.Length; v++)
            {
                if (_vertexNormals[v].TryNormalize(Tolerance.Default, out var n))
                    _vertexNormals[v] = n;
            }
        }

        _bvh = Bvh.Build(boxes);
        _bounds = triangulated.ComputeBounds();
    }

    public override Aabb Bounds => _bounds;

    public override double Evaluate(in Vector3d point)
    {
        var p = point;
        // Struct metric through the generic Nearest — no closure/boxing allocation in the
        // hottest kernel path (locked by MeshSdfTests.Evaluate_SteadyState_DoesNotAllocate).
        var metric = new TriangleDistance(this, p);
        _bvh.Nearest(point, ref metric, out int triangle, out double distance);

        if (_winding is not null)
            return _winding.FastWindingNumber(p) > 0.5 ? -distance : distance;

        var closest = ClosestPoint(triangle, p, out var region);
        var pseudonormal = region switch
        {
            TriangleRegion.Face => _faceNormals![triangle],
            TriangleRegion.EdgeAb => _edgeNormals![triangle * 3],
            TriangleRegion.EdgeBc => _edgeNormals![triangle * 3 + 1],
            TriangleRegion.EdgeCa => _edgeNormals![triangle * 3 + 2],
            TriangleRegion.VertexA => _vertexNormals![_vertexIds![triangle * 3]],
            TriangleRegion.VertexB => _vertexNormals![_vertexIds![triangle * 3 + 1]],
            _ => _vertexNormals![_vertexIds![triangle * 3 + 2]],
        };
        return (p - closest).Dot(pseudonormal) >= 0 ? distance : -distance;
    }

    /// <summary>Allocation-free point-to-triangle distance metric for <see cref="Bvh.Nearest{TMetric}"/>.</summary>
    private readonly struct TriangleDistance(MeshSdf sdf, Vector3d point) : IBvhDistance
    {
        public double DistanceTo(int item) =>
            Math.Sqrt(sdf.ClosestPoint(item, point, out _).DistanceSquaredTo(point));
    }

    /// <summary>
    /// Closest point on triangle <paramref name="triangle"/> and the feature it lies on
    /// (<see cref="Distance3d.ClosestPointOnTriangle(in Vector3d, in Vector3d, in Vector3d, in Vector3d, out TriangleRegion)"/>).
    /// The region is not incidental here: the sign comes from the angle-weighted
    /// pseudonormal of whichever feature is closest, so face, edge and vertex hits select
    /// different normals.
    /// </summary>
    private Vector3d ClosestPoint(int triangle, in Vector3d p, out TriangleRegion region) =>
        Distance3d.ClosestPointOnTriangle(
            p, _cornerA[triangle], _cornerB[triangle], _cornerC[triangle], out region);
}
