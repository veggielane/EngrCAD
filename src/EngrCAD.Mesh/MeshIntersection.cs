using EngrCAD.Core;
using EngrCAD.Core.Spatial;

namespace EngrCAD.Mesh;

/// <summary>
/// One triangle–triangle intersection segment: which face of each mesh produced it, and
/// the segment's two endpoints. Endpoints lie on both triangles to round-off.
/// </summary>
/// <param name="FaceA">Face index in the first (triangulated) mesh.</param>
/// <param name="FaceB">Face index in the second (triangulated) mesh.</param>
/// <param name="Transversal">
/// True when EACH triangle passes through the other's plane — corners strictly on both
/// sides — rather than merely reaching it. That is the difference between two solids
/// interpenetrating and two solids resting against each other, and it cannot be read off the
/// segment itself: a box seated on another produces a perfectly good contact rim, because
/// every side face of the upper box meets the lower box's top plane along its bottom edge.
/// The segment is real either way; only its meaning differs.
/// </param>
public readonly record struct MeshIntersectionSegment(
    int FaceA, int FaceB, Vector3d Start, Vector3d End, bool Transversal)
{
    public double Length => Start.DistanceTo(End);
}

/// <summary>
/// What two surfaces do where they meet, in the three flavours that are genuinely different
/// facts: <see cref="Segments"/> is every curve piece where they meet at all,
/// <see cref="Crosses"/> is whether any of it is a real interpenetration
/// (<see cref="MeshIntersectionSegment.Transversal"/>), and <see cref="CoplanarOverlaps"/>
/// is where they lie flush and therefore produce no curve at all.
/// <para>
/// Keeping them apart is the point. Two parts resting on each other share boundary without
/// interfering, and a seated part produces BOTH a flush overlap (the mating faces) and a
/// perfectly good contact rim of segments (every side face of the upper part reaches the
/// lower part's top plane along its bottom edge). Folding either into "they intersect" makes
/// the query useless for the thing it is for.
/// </para>
/// </summary>
public sealed class MeshIntersectionReport(
    IReadOnlyList<MeshIntersectionSegment> segments,
    IReadOnlyList<(int FaceA, int FaceB)> coplanarOverlaps)
{
    /// <summary>Every curve piece where the two surfaces meet, in an unspecified but
    /// deterministic order. Includes grazing contact — see
    /// <see cref="MeshIntersectionSegment.Transversal"/>.</summary>
    public IReadOnlyList<MeshIntersectionSegment> Segments { get; } = segments;

    /// <summary>Face pairs that are coplanar and share interior area (flush contact, no curve).</summary>
    public IReadOnlyList<(int FaceA, int FaceB)> CoplanarOverlaps { get; } = coplanarOverlaps;

    /// <summary>True when the surfaces meet at all, however they meet.</summary>
    public bool Meets => Segments.Count > 0 || CoplanarOverlaps.Count > 0;

    /// <summary>
    /// True when the two surfaces interpenetrate — some segment is
    /// <see cref="MeshIntersectionSegment.Transversal"/>. This is the clash-detection
    /// question, and it is deliberately false for parts that merely rest against each other.
    /// </summary>
    public bool Crosses
    {
        get
        {
            foreach (var segment in Segments)
            {
                if (segment.Transversal)
                    return true;
            }
            return false;
        }
    }

    /// <summary>True when the two surfaces make contact without interpenetrating — flush
    /// faces, or a contact rim with no transversal segment in it.</summary>
    public bool Touches => Meets && !Crosses;

    /// <summary>Total length of the transversal curve; a rough severity measure for a clash
    /// report, and zero for parts that only touch.</summary>
    public double CurveLength
    {
        get
        {
            double total = 0;
            foreach (var segment in Segments)
            {
                if (segment.Transversal)
                    total += segment.Length;
            }
            return total;
        }
    }
}

/// <summary>
/// Surface-crossing queries between triangle meshes: the broad phase is
/// <see cref="Bvh.QueryOverlap"/> over per-triangle boxes, the narrow phase is the Möller
/// interval test. This is the <em>query</em> layer — it answers where two surfaces meet and
/// nothing more.
/// <para>
/// <b>Why this is not <see cref="MeshMeshCut"/>.</b> The boolean's cut computes the same
/// segments but is bound by a far stronger requirement: every point must be derived from a
/// canonical (edge, plane) pair so that both meshes and both adjacent faces produce
/// bit-identical coordinates, and each crossing must carry the half-edge and parameter the
/// imprint will poke. None of that machinery says anything about <em>where the surfaces
/// meet</em>, and a caller asking that question should not pay for an imprint it will throw
/// away. So the narrow phase here is the plain interval test, and only the coplanar-overlap
/// decision — a separating-axis test whose epsilon scaling is subtle — is shared.
/// </para>
/// <para>
/// Degeneracy guards are relative to the operands' extent
/// (<see cref="MeshMeshCut.RelativeEpsilon"/>, the scale-free tier of the epsilon ladder),
/// so the answers do not change when a model is scaled.
/// </para>
/// </summary>
public static class MeshIntersection
{
    /// <summary>
    /// Where two meshes' surfaces meet. Both are triangulated first, so face indices in the
    /// report refer to <see cref="HalfEdgeMesh.Triangulated"/> of the inputs.
    /// </summary>
    public static MeshIntersectionReport Between(
        HalfEdgeMesh a, HalfEdgeMesh b, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var operandA = new Operand(a);
        var operandB = new Operand(b);
        return Collect(operandA, operandB, self: false, stopAtFirst: false, progress);
    }

    /// <summary>
    /// Whether the two surfaces interpenetrate anywhere — the interference question,
    /// answered with an early exit at the first transversal pair rather than by collecting
    /// the whole curve. Contact without interpenetration (flush faces, a seated part's
    /// contact rim) is NOT a crossing; use <see cref="Between"/> when that distinction
    /// matters.
    /// </summary>
    public static bool Crosses(HalfEdgeMesh a, HalfEdgeMesh b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Collect(new Operand(a), new Operand(b), self: false, stopAtFirst: true, null).Crosses;
    }

    /// <summary>
    /// Where a mesh's surface meets itself. Faces that share a vertex are excluded: they
    /// meet along their shared edge or at their shared corner by construction, which is what
    /// a mesh IS, not a defect. Every reported pair has <c>FaceA &lt; FaceB</c>.
    /// </summary>
    public static MeshIntersectionReport WithinItself(HalfEdgeMesh mesh, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var operand = new Operand(mesh);
        return Collect(operand, operand, self: true, stopAtFirst: false, progress);
    }

    private static MeshIntersectionReport Collect(
        Operand a, Operand b, bool self, bool stopAtFirst, ProgressCancel? progress)
    {
        var segments = new List<MeshIntersectionSegment>();
        var coplanar = new List<(int, int)>();
        double scale = Math.Max(a.Extent, b.Extent);
        if (scale <= 0)
            return new MeshIntersectionReport(segments, coplanar);
        double epsilon = MeshMeshCut.RelativeEpsilon * scale;

        progress?.ThrowIfCancelled();
        var pairs = new List<(int Item, int OtherItem)>();
        a.Tree.QueryOverlap(b.Tree, pairs);
        progress?.Report(0.5);

        int examined = 0;
        foreach (var (fa, fb) in pairs)
        {
            if (progress is not null && (++examined & 1023) == 0)
            {
                progress.ThrowIfCancelled();
                progress.Report(0.5 + 0.5 * examined / pairs.Count);
            }
            // Querying a tree against itself reports (i, i) and both orderings of every
            // distinct pair, so a self-query keeps one ordering and drops adjacency.
            if (self && (fa >= fb || a.SharesVertex(fa, fb)))
                continue;

            var outcome = Meet(a, fa, b, fb, epsilon, out var start, out var end, out bool transversal);
            if (outcome == Outcome.CoplanarOverlap)
            {
                coplanar.Add((fa, fb));
            }
            else if (outcome == Outcome.Segment)
            {
                segments.Add(new MeshIntersectionSegment(fa, fb, start, end, transversal));
                if (stopAtFirst && transversal)
                    break;
            }
        }

        progress?.Report(1);
        return new MeshIntersectionReport(segments, coplanar);
    }

    private enum Outcome
    {
        None,
        Segment,
        CoplanarOverlap,
    }

    /// <summary>
    /// Möller's interval test: reject by the corners' signed distances to the other plane,
    /// then intersect the two triangles' intervals on the planes' common line. The endpoints
    /// are two of the four plane crossings — computed here for the answer, not interned for
    /// a later imprint.
    /// </summary>
    private static Outcome Meet(
        Operand a, int faceA, Operand b, int faceB, double epsilon,
        out Vector3d start, out Vector3d end, out bool transversal)
    {
        start = default;
        end = default;
        transversal = false;
        if (!a.TryPlane(faceA, out var normalA, out var originA) ||
            !b.TryPlane(faceB, out var normalB, out var originB))
            return Outcome.None; // sliver face: no plane to intersect

        var cornersA = a.Corners(faceA);
        var cornersB = b.Corners(faceB);

        Span<double> distA = stackalloc double[3];
        Span<double> distB = stackalloc double[3];
        if (!Straddles(cornersA, normalB, originB, epsilon, distA, out int onA, out bool throughA) ||
            !Straddles(cornersB, normalA, originA, epsilon, distB, out int onB, out bool throughB))
            return Outcome.None;

        if (onA == 3 || onB == 3)
        {
            return MeshMeshCut.CoplanarTrianglesOverlap(cornersA, cornersB, normalA, originA, epsilon)
                ? Outcome.CoplanarOverlap
                : Outcome.None; // coplanar but only touching along a shared edge or corner
        }

        var direction = normalA.Cross(normalB);
        double length = direction.Length;
        if (length <= MeshMeshCut.RelativeEpsilon)
            return Outcome.None; // parallel planes, and not coplanar (settled above)
        direction /= length;

        Span<Vector3d> crossA = stackalloc Vector3d[2];
        Span<Vector3d> crossB = stackalloc Vector3d[2];
        if (PlaneCrossings(cornersA, distA, epsilon, crossA) < 2 ||
            PlaneCrossings(cornersB, distB, epsilon, crossB) < 2)
            return Outcome.None; // the triangle only touches the other plane at a point

        Order(crossA, direction);
        Order(crossB, direction);
        var low = direction.Dot(crossA[0]) >= direction.Dot(crossB[0]) ? crossA[0] : crossB[0];
        var high = direction.Dot(crossA[1]) <= direction.Dot(crossB[1]) ? crossA[1] : crossB[1];
        if (direction.Dot(high) - direction.Dot(low) <= epsilon)
            return Outcome.None; // disjoint intervals, or a touch too short to matter

        start = low;
        end = high;
        // Interpenetration needs BOTH triangles to pass through the other's plane. One-sided
        // is contact: a box seated on another has every upper side face reaching the lower
        // top plane along its bottom edge (two corners on it, one above) while the lower top
        // face happily straddles the side face's plane — so testing only one side would call
        // every seated part a clash.
        transversal = throughA && throughB;
        return Outcome.Segment;
    }

    /// <summary>Signed distances of a triangle's corners to a plane; false when all three
    /// sit strictly on one side. <paramref name="on"/> counts the corners in the plane, and
    /// <paramref name="through"/> is set when corners sit strictly on BOTH sides — the
    /// triangle passes through the plane rather than reaching it.</summary>
    private static bool Straddles(
        ReadOnlySpan<Vector3d> corners, in Vector3d normal, in Vector3d origin, double epsilon,
        Span<double> distances, out int on, out bool through)
    {
        int above = 0, below = 0;
        on = 0;
        for (int i = 0; i < 3; i++)
        {
            distances[i] = normal.Dot(corners[i] - origin);
            if (distances[i] > epsilon) above++;
            else if (distances[i] < -epsilon) below++;
            else on++;
        }
        through = above > 0 && below > 0;
        return above != 3 && below != 3;
    }

    /// <summary>The (at most two) points where a triangle meets a plane. Corners within
    /// <paramref name="epsilon"/> of the plane are reported as themselves.</summary>
    private static int PlaneCrossings(
        ReadOnlySpan<Vector3d> corners, ReadOnlySpan<double> distances, double epsilon,
        Span<Vector3d> buffer)
    {
        int count = 0;
        for (int i = 0; i < 3 && count < 2; i++)
        {
            if (Math.Abs(distances[i]) <= epsilon)
                buffer[count++] = corners[i];
        }
        for (int i = 0; i < 3 && count < 2; i++)
        {
            int j = (i + 1) % 3;
            if (Math.Abs(distances[i]) <= epsilon || Math.Abs(distances[j]) <= epsilon)
                continue;
            if (distances[i] > 0 == distances[j] > 0)
                continue;
            double t = distances[i] / (distances[i] - distances[j]);
            buffer[count++] = corners[i] + (corners[j] - corners[i]) * t;
        }
        return count;
    }

    private static void Order(Span<Vector3d> pair, in Vector3d direction)
    {
        if (direction.Dot(pair[0]) > direction.Dot(pair[1]))
            (pair[0], pair[1]) = (pair[1], pair[0]);
    }

    /// <summary>Triangulated operand: corner positions, per-face boxes, the tree, and the
    /// vertex ids a self-query needs to recognize adjacency.</summary>
    private sealed class Operand
    {
        private readonly Vector3d[] _positions;
        private readonly int[][] _triangles;

        public Operand(HalfEdgeMesh mesh)
        {
            var (positions, faces) = mesh.Triangulated().ToIndexed();
            _positions = [.. positions];
            _triangles = [.. faces];
            var boxes = new Aabb[_triangles.Length];
            var bounds = Aabb.Empty;
            for (int f = 0; f < _triangles.Length; f++)
            {
                var tri = _triangles[f];
                boxes[f] = Aabb.Empty
                    .Union(_positions[tri[0]]).Union(_positions[tri[1]]).Union(_positions[tri[2]]);
                bounds = bounds.Union(boxes[f]);
            }
            Tree = Bvh.Build(boxes);
            Extent = bounds.IsEmpty ? 0 : bounds.Size[bounds.LongestAxis];
        }

        public Bvh Tree { get; }

        public double Extent { get; }

        public Vector3d[] Corners(int face)
        {
            var tri = _triangles[face];
            return [_positions[tri[0]], _positions[tri[1]], _positions[tri[2]]];
        }

        public bool SharesVertex(int faceA, int faceB)
        {
            var x = _triangles[faceA];
            var y = _triangles[faceB];
            foreach (int i in x)
            {
                foreach (int j in y)
                {
                    if (i == j)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Unit normal and a point of the face's plane; false for a sliver whose
        /// area is below the relative degeneracy guard (its plane is round-off).</summary>
        public bool TryPlane(int face, out Vector3d normal, out Vector3d origin)
        {
            var tri = _triangles[face];
            var a = _positions[tri[0]];
            var b = _positions[tri[1]];
            var c = _positions[tri[2]];
            origin = a;
            var cross = (b - a).Cross(c - a);
            // Orient2d-style scaling: the cross product is twice an AREA, so the length
            // guard has to be multiplied by the triangle's own size to compare against it.
            double size = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
            double guard = MeshMeshCut.RelativeEpsilon * Extent * size;
            if (cross.Length <= guard)
            {
                normal = default;
                return false;
            }
            normal = cross / cross.Length;
            return true;
        }
    }
}
