using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Mesh simplification by iterative edge collapse with quadric error metrics
/// (Garland–Heckbert). Collapses the cheapest edge (placing the merged vertex at the
/// quadric-optimal point) until the face budget is met, guarded by manifold link
/// conditions and a normal-flip check. Boundary vertices are never collapsed, so open
/// meshes keep their boundary exactly. Polygon meshes are triangulated first.
/// <para>
/// The topology layer is <see cref="EditableMesh"/>: <see cref="EditableMesh.CollapseEdge"/>
/// is the canonical guarded half-edge collapse, and this is what it exists for. It replaced a
/// private indexed-face-set scratch representation (a <c>HashSet</c> of faces per vertex plus
/// its own link-condition check) after a measured comparison — <c>MeshDecimatorQualityTests</c>
/// carries the numbers: <b>bit-identical output</b> on twelve fixture/budget pairs, <b>0.84x</b>
/// the time, and correct at 1e-5 scale, where the old path lost 91% of the volume because it
/// normalized face normals against the absolute 1e-9 weld tolerance — an absolute epsilon on a
/// cross product, i.e. an area, so below ~1e-4 scale every face was "degenerate" and
/// contributed no quadric at all.
/// </para>
/// </summary>
public static class MeshDecimator
{
    /// <param name="mesh">The mesh to simplify (triangulated automatically).</param>
    /// <param name="targetFaceCount">Face budget to collapse down to (≥ 4).</param>
    /// <param name="progress">
    /// Optional cooperative progress/cancellation; cancellation throws
    /// <see cref="OperationCanceledException"/> and discards the partial result.
    /// </param>
    public static HalfEdgeMesh Decimate(HalfEdgeMesh mesh, int targetFaceCount, ProgressCancel? progress = null)
    {
        if (targetFaceCount < 4)
            throw new ArgumentOutOfRangeException(nameof(targetFaceCount));

        if (mesh.Faces.Any(f => f.Degree != 3))
            mesh = mesh.Triangulated();
        if (mesh.FaceCount <= targetFaceCount)
            return mesh;

        var state = new DecimationState(mesh);
        state.CollapseUntil(targetFaceCount, progress);
        return state.BuildResult();
    }

    private sealed class DecimationState
    {
        private readonly EditableMesh _mesh;
        private readonly Quadric[] _quadrics;
        private readonly bool[] _boundary;
        private readonly double _degenerateAreaSquared;
        private readonly IndexPriorityQueue _queue = new();
        private readonly Dictionary<(int, int), int> _edgeIds = [];
        private readonly List<(int A, int B, Vector3d Optimal)> _edgeData = [];
        private int[] _ring = new int[16];

        public DecimationState(HalfEdgeMesh mesh)
        {
            _mesh = EditableMesh.FromMesh(mesh);
            _quadrics = new Quadric[mesh.VertexCount];
            _boundary = new bool[mesh.VertexCount];

            // Scale-free degeneracy floor, replacing the original's absolute 1e-24 on a
            // SQUARED AREA: an absolute area epsilon fails quadratically with model scale
            // (the BSP lesson). 1e-13 x extent is the exact boolean's tier; squared twice
            // because the guarded quantity is |cross|^2 = (2 x area)^2.
            double extent = Math.Max(mesh.ComputeBounds().Size.Length, double.Epsilon);
            double doubledArea = 2 * 1e-13 * extent * extent;
            _degenerateAreaSquared = doubledArea * doubledArea;

            foreach (var vertex in mesh.Vertices)
                _boundary[vertex.Index] = !vertex.IsIsolated && vertex.IsBoundary;

            var triangles = new (int A, int B, int C)[mesh.FaceCount];
            foreach (var face in mesh.Faces)
            {
                int h0 = face.AnyHalfEdge.Index;
                int a = mesh.HeOrigin(h0), b = mesh.HeOrigin(mesh.HeNext(h0)), c = mesh.HeOrigin(mesh.HePrev(h0));
                triangles[face.Index] = (a, b, c);
                var pa = mesh.GetPosition(a);
                var raw = (mesh.GetPosition(b) - pa).Cross(mesh.GetPosition(c) - pa);
                double length = raw.Length;
                // Exact-zero guard, NOT Tolerance.Default: the original normalizes this cross
                // product against the absolute 1e-9 weld tier, so every face of a model below
                // ~1e-4 scale is treated as degenerate and contributes no quadric at all.
                if (length == 0)
                    continue;
                var n = raw / length;
                var q = Quadric.FromPlane(n, -n.Dot(pa), length * 0.5);
                _quadrics[a] += q;
                _quadrics[b] += q;
                _quadrics[c] += q;
            }

            // Seeding is a SECOND pass, in the original's order (per face, a-b, b-c, c-a):
            // an edge's priority is its two endpoints' summed quadrics, which are only
            // complete once every face has contributed. Folding this into the loop above
            // silently keys the whole initial queue off partial quadrics — measured as a
            // 2.4x worse approximation error at light decimation, where the early (cheapest)
            // choices dominate the result.
            var seeded = new HashSet<(int, int)>();
            foreach (var (a, b, c) in triangles)
            {
                TrySeed(a, b, seeded);
                TrySeed(b, c, seeded);
                TrySeed(c, a, seeded);
            }
        }

        private void TrySeed(int a, int b, HashSet<(int, int)> seeded)
        {
            var key = EdgeKey(a, b);
            if (seeded.Add(key))
                Enqueue(key.Item1, key.Item2);
        }

        private static (int, int) EdgeKey(int a, int b) => (Math.Min(a, b), Math.Max(a, b));

        private void Enqueue(int a, int b)
        {
            if (_boundary[a] || _boundary[b])
                return;
            var q = _quadrics[a] + _quadrics[b];
            var optimal = OptimalPosition(q, a, b);
            var key = EdgeKey(a, b);
            if (!_edgeIds.TryGetValue(key, out int id))
            {
                id = _edgeData.Count;
                _edgeIds.Add(key, id);
                _edgeData.Add(default);
            }
            _edgeData[id] = (a, b, optimal);
            _queue.EnqueueOrUpdate(id, Math.Max(0, q.Error(optimal)));
        }

        private Vector3d OptimalPosition(in Quadric q, int a, int b)
        {
            if (q.TryOptimize(out var v))
                return v;
            var pa = _mesh.GetPosition(a);
            var pb = _mesh.GetPosition(b);
            var mid = (pa + pb) * 0.5;
            var best = mid;
            double bestError = q.Error(mid);
            if (q.Error(pa) < bestError)
            {
                best = pa;
                bestError = q.Error(pa);
            }
            if (q.Error(pb) < bestError)
                best = pb;
            return best;
        }

        public void CollapseUntil(int targetFaceCount, ProgressCancel? progress)
        {
            int initialAlive = _mesh.FaceCount;
            int sinceCheckpoint = 0;
            while (_mesh.FaceCount > targetFaceCount && _queue.TryDequeue(out int id, out _))
            {
                var (a, b, optimal) = _edgeData[id];
                if (_mesh.IsVertex(a) && _mesh.IsVertex(b))
                    TryCollapse(a, b, optimal);

                if (progress is not null && ++sinceCheckpoint >= 256)
                {
                    sinceCheckpoint = 0;
                    progress.ThrowIfCancelled();
                    progress.Report((double)(initialAlive - _mesh.FaceCount) / (initialAlive - targetFaceCount));
                }
            }
            progress?.ThrowIfCancelled();
            progress?.Report(1);
        }

        private void TryCollapse(int a, int b, in Vector3d optimal)
        {
            int he = _mesh.FindHalfEdge(a, b);
            if (he < 0)
                return;
            int face0 = _mesh.FaceOf(he);
            int face1 = _mesh.FaceOf(_mesh.Twin(he));
            if (face0 < 0 || face1 < 0)
                return; // interior edges only, as in the original

            if (!RingSurvivesMove(a, optimal, face0, face1) || !RingSurvivesMove(b, optimal, face0, face1))
                return;

            // Neighbours of b lose their edges to it; collect them before the collapse so the
            // stale queue entries can be dropped.
            int count = FillRing(b);
            Span<int> neighbors = count <= 64 ? stackalloc int[count] : new int[count];
            for (int i = 0; i < count; i++)
                neighbors[i] = _mesh.Origin(_mesh.Twin(_ring[i]));

            // Link condition, bow-ties, tetrahedra and last-triangle cases are the operator's
            // guards; a refusal leaves the mesh untouched.
            if (_mesh.CollapseEdge(he, out _) != MeshOperationResult.Ok)
                return;
            _mesh.SetPosition(a, optimal);
            _quadrics[a] += _quadrics[b];

            foreach (int neighbor in neighbors)
            {
                if (_edgeIds.TryGetValue(EdgeKey(b, neighbor), out int id) && _queue.Contains(id))
                    _queue.Remove(id);
            }
            count = FillRing(a);
            for (int i = 0; i < count; i++)
            {
                int neighbor = _mesh.Origin(_mesh.Twin(_ring[i]));
                Enqueue(Math.Min(a, neighbor), Math.Max(a, neighbor));
            }
        }

        /// <summary>Normal-flip and degeneracy guard, identical in form to the original's.</summary>
        private bool RingSurvivesMove(int vertex, in Vector3d newPosition, int skipFace0, int skipFace1)
        {
            int count = FillRing(vertex);
            for (int i = 0; i < count; i++)
            {
                int face = _mesh.FaceOf(_ring[i]);
                if (face < 0 || face == skipFace0 || face == skipFace1)
                    continue;
                int h0 = _mesh.FaceHalfEdge(face);
                int h1 = _mesh.Next(h0);
                int h2 = _mesh.Next(h1);
                int v0 = _mesh.Origin(h0), v1 = _mesh.Origin(h1), v2 = _mesh.Origin(h2);
                var p0 = _mesh.GetPosition(v0);
                var p1 = _mesh.GetPosition(v1);
                var p2 = _mesh.GetPosition(v2);
                var before = (p1 - p0).Cross(p2 - p0);
                var q0 = v0 == vertex ? newPosition : p0;
                var q1 = v1 == vertex ? newPosition : p1;
                var q2 = v2 == vertex ? newPosition : p2;
                var after = (q1 - q0).Cross(q2 - q0);
                if (after.LengthSquared < _degenerateAreaSquared || before.Dot(after) <= 0)
                    return false;
            }
            return true;
        }

        private int FillRing(int vertex)
        {
            int count;
            while ((count = _mesh.OutgoingHalfEdges(vertex, _ring)) < 0)
                _ring = new int[_ring.Length * 2];
            return count;
        }

        public HalfEdgeMesh BuildResult() => _mesh.ToMesh();
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
