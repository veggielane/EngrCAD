using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Mesh simplification by iterative edge collapse with quadric error metrics
/// (Garland–Heckbert). Collapses the cheapest edge (placing the merged vertex at the
/// quadric-optimal point) until the face budget is met, guarded by manifold link
/// conditions and a normal-flip check. Boundary vertices are never collapsed, so open
/// meshes keep their boundary exactly. Polygon meshes are triangulated first.
/// </summary>
public static class MeshDecimator
{
    public static HalfEdgeMesh Decimate(HalfEdgeMesh mesh, int targetFaceCount)
    {
        if (targetFaceCount < 4)
            throw new ArgumentOutOfRangeException(nameof(targetFaceCount));

        if (mesh.Faces.Any(f => f.Degree != 3))
            mesh = mesh.Triangulated();
        if (mesh.FaceCount <= targetFaceCount)
            return mesh;

        var state = new DecimationState(mesh);
        state.CollapseUntil(targetFaceCount);
        return state.BuildResult();
    }

    private sealed class DecimationState
    {
        private readonly List<Vector3d> _positions;
        private readonly List<(int A, int B, int C)> _faces;
        private readonly bool[] _faceAlive;
        private readonly bool[] _vertexAlive;
        private readonly bool[] _vertexBoundary;
        private readonly HashSet<int>[] _vertexFaces;
        private readonly Quadric[] _quadrics;
        private readonly int[] _stamps;
        private readonly PriorityQueue<Candidate, double> _queue = new();
        private int _aliveFaceCount;

        private readonly record struct Candidate(int A, int B, int StampA, int StampB, Vector3d Optimal);

        public DecimationState(HalfEdgeMesh mesh)
        {
            var (positions, faces) = mesh.ToIndexed();
            _positions = [.. positions];
            _faces = faces.Select(f => (f[0], f[1], f[2])).ToList();
            _faceAlive = new bool[_faces.Count];
            Array.Fill(_faceAlive, true);
            _aliveFaceCount = _faces.Count;
            _vertexAlive = new bool[_positions.Count];
            Array.Fill(_vertexAlive, true);
            _vertexBoundary = new bool[_positions.Count];
            foreach (var vertex in mesh.Vertices)
                _vertexBoundary[vertex.Index] = !vertex.IsIsolated && vertex.IsBoundary;
            _stamps = new int[_positions.Count];

            _vertexFaces = new HashSet<int>[_positions.Count];
            for (int v = 0; v < _positions.Count; v++)
                _vertexFaces[v] = [];
            for (int f = 0; f < _faces.Count; f++)
            {
                var (a, b, c) = _faces[f];
                _vertexFaces[a].Add(f);
                _vertexFaces[b].Add(f);
                _vertexFaces[c].Add(f);
            }

            // Area-weighted plane quadrics.
            _quadrics = new Quadric[_positions.Count];
            for (int f = 0; f < _faces.Count; f++)
            {
                var (a, b, c) = _faces[f];
                var raw = (_positions[b] - _positions[a]).Cross(_positions[c] - _positions[a]);
                double area = raw.Length * 0.5;
                if (!raw.TryNormalize(Tolerance.Default, out var n))
                    continue;
                var q = Quadric.FromPlane(n, -n.Dot(_positions[a]), area);
                _quadrics[a] += q;
                _quadrics[b] += q;
                _quadrics[c] += q;
            }

            var seeded = new HashSet<(int, int)>();
            for (int f = 0; f < _faces.Count; f++)
            {
                var (a, b, c) = _faces[f];
                TrySeed(a, b, seeded);
                TrySeed(b, c, seeded);
                TrySeed(c, a, seeded);
            }
        }

        private void TrySeed(int a, int b, HashSet<(int, int)> seeded)
        {
            var key = a < b ? (a, b) : (b, a);
            if (seeded.Add(key))
                Enqueue(key.Item1, key.Item2);
        }

        private void Enqueue(int a, int b)
        {
            if (_vertexBoundary[a] || _vertexBoundary[b])
                return;
            var q = _quadrics[a] + _quadrics[b];
            var optimal = OptimalPosition(q, a, b);
            _queue.Enqueue(new Candidate(a, b, _stamps[a], _stamps[b], optimal),
                Math.Max(0, q.Error(optimal)));
        }

        private Vector3d OptimalPosition(in Quadric q, int a, int b)
        {
            if (q.TryOptimize(out var v))
                return v;
            // Singular quadric (flat regions): best of endpoints and midpoint.
            var mid = (_positions[a] + _positions[b]) * 0.5;
            var best = mid;
            double bestError = q.Error(mid);
            if (q.Error(_positions[a]) < bestError)
            {
                best = _positions[a];
                bestError = q.Error(_positions[a]);
            }
            if (q.Error(_positions[b]) < bestError)
                best = _positions[b];
            return best;
        }

        public void CollapseUntil(int targetFaceCount)
        {
            while (_aliveFaceCount > targetFaceCount && _queue.TryDequeue(out var candidate, out _))
            {
                var (a, b, stampA, stampB, optimal) = candidate;
                if (!_vertexAlive[a] || !_vertexAlive[b] ||
                    _stamps[a] != stampA || _stamps[b] != stampB)
                    continue;
                TryCollapse(a, b, optimal);
            }
        }

        private void TryCollapse(int a, int b, in Vector3d optimal)
        {
            // The faces shared by a and b vanish; an interior manifold edge has exactly 2.
            var sharedFaces = _vertexFaces[a].Where(_vertexFaces[b].Contains).ToList();
            if (sharedFaces.Count != 2)
                return;

            // Link condition: the vertex neighborhoods may only meet at the two vertices
            // opposite the collapsing edge, or the collapse pinches the surface.
            var linkA = NeighborVertices(a);
            linkA.IntersectWith(NeighborVertices(b));
            if (linkA.Count != 2)
                return;

            // Normal-flip / degeneracy guard on every surviving incident face.
            foreach (int f in _vertexFaces[a].Concat(_vertexFaces[b]))
            {
                if (sharedFaces.Contains(f))
                    continue;
                var (fa, fb, fc) = _faces[f];
                var p0 = _positions[fa];
                var p1 = _positions[fb];
                var p2 = _positions[fc];
                var before = (p1 - p0).Cross(p2 - p0);
                var q0 = fa == a || fa == b ? optimal : p0;
                var q1 = fb == a || fb == b ? optimal : p1;
                var q2 = fc == a || fc == b ? optimal : p2;
                var after = (q1 - q0).Cross(q2 - q0);
                if (after.LengthSquared < 1e-24 || before.Dot(after) <= 0)
                    return;
            }

            // Execute.
            _positions[a] = optimal;
            _quadrics[a] += _quadrics[b];
            foreach (int f in sharedFaces)
            {
                _faceAlive[f] = false;
                _aliveFaceCount--;
                var (fa, fb, fc) = _faces[f];
                _vertexFaces[fa].Remove(f);
                _vertexFaces[fb].Remove(f);
                _vertexFaces[fc].Remove(f);
            }
            foreach (int f in _vertexFaces[b].ToList())
            {
                var (fa, fb, fc) = _faces[f];
                _faces[f] = (fa == b ? a : fa, fb == b ? a : fb, fc == b ? a : fc);
                _vertexFaces[a].Add(f);
            }
            _vertexAlive[b] = false;
            _stamps[a]++;
            _stamps[b]++;

            foreach (int neighbor in NeighborVertices(a))
                Enqueue(Math.Min(a, neighbor), Math.Max(a, neighbor));
        }

        private HashSet<int> NeighborVertices(int v)
        {
            var neighbors = new HashSet<int>();
            foreach (int f in _vertexFaces[v])
            {
                var (a, b, c) = _faces[f];
                neighbors.Add(a);
                neighbors.Add(b);
                neighbors.Add(c);
            }
            neighbors.Remove(v);
            return neighbors;
        }

        public HalfEdgeMesh BuildResult()
        {
            var remap = new int[_positions.Count];
            Array.Fill(remap, -1);
            var positions = new List<Vector3d>();
            var faces = new List<int[]>();
            for (int f = 0; f < _faces.Count; f++)
            {
                if (!_faceAlive[f])
                    continue;
                var (a, b, c) = _faces[f];
                if (a == b || b == c || c == a)
                    continue;
                int[] loop = [Map(a), Map(b), Map(c)];
                faces.Add(loop);
            }
            return HalfEdgeMesh.Build(positions, faces);

            int Map(int v)
            {
                if (remap[v] < 0)
                {
                    remap[v] = positions.Count;
                    positions.Add(_positions[v]);
                }
                return remap[v];
            }
        }
    }

    /// <summary>Symmetric 4×4 error quadric (10 unique coefficients).</summary>
    private struct Quadric
    {
        private double _q11, _q12, _q13, _q14, _q22, _q23, _q24, _q33, _q34, _q44;

        public static Quadric FromPlane(in Vector3d n, double d, double weight)
        {
            var q = new Quadric();
            q._q11 = weight * n.X * n.X;
            q._q12 = weight * n.X * n.Y;
            q._q13 = weight * n.X * n.Z;
            q._q14 = weight * n.X * d;
            q._q22 = weight * n.Y * n.Y;
            q._q23 = weight * n.Y * n.Z;
            q._q24 = weight * n.Y * d;
            q._q33 = weight * n.Z * n.Z;
            q._q34 = weight * n.Z * d;
            q._q44 = weight * d * d;
            return q;
        }

        public static Quadric operator +(in Quadric a, in Quadric b) => new()
        {
            _q11 = a._q11 + b._q11,
            _q12 = a._q12 + b._q12,
            _q13 = a._q13 + b._q13,
            _q14 = a._q14 + b._q14,
            _q22 = a._q22 + b._q22,
            _q23 = a._q23 + b._q23,
            _q24 = a._q24 + b._q24,
            _q33 = a._q33 + b._q33,
            _q34 = a._q34 + b._q34,
            _q44 = a._q44 + b._q44,
        };

        public readonly double Error(in Vector3d v) =>
            _q11 * v.X * v.X + 2 * _q12 * v.X * v.Y + 2 * _q13 * v.X * v.Z + 2 * _q14 * v.X +
            _q22 * v.Y * v.Y + 2 * _q23 * v.Y * v.Z + 2 * _q24 * v.Y +
            _q33 * v.Z * v.Z + 2 * _q34 * v.Z +
            _q44;

        /// <summary>Minimizer of the quadric: solve the 3×3 system ∇error = 0 (Cramer).</summary>
        public readonly bool TryOptimize(out Vector3d v)
        {
            double det =
                _q11 * (_q22 * _q33 - _q23 * _q23) -
                _q12 * (_q12 * _q33 - _q23 * _q13) +
                _q13 * (_q12 * _q23 - _q22 * _q13);
            // Scale-aware singularity threshold.
            double scale = Math.Max(Math.Abs(_q11), Math.Max(Math.Abs(_q22), Math.Abs(_q33)));
            if (Math.Abs(det) <= 1e-10 * scale * scale * scale)
            {
                v = default;
                return false;
            }

            double bx = -_q14, by = -_q24, bz = -_q34;
            double inv = 1.0 / det;
            v = new Vector3d(
                (bx * (_q22 * _q33 - _q23 * _q23) - _q12 * (by * _q33 - _q23 * bz) + _q13 * (by * _q23 - _q22 * bz)) * inv,
                (_q11 * (by * _q33 - _q23 * bz) - bx * (_q12 * _q33 - _q23 * _q13) + _q13 * (_q12 * bz - by * _q13)) * inv,
                (_q11 * (_q22 * bz - by * _q23) - _q12 * (_q12 * bz - by * _q13) + bx * (_q12 * _q23 - _q22 * _q13)) * inv);
            return true;
        }
    }
}
