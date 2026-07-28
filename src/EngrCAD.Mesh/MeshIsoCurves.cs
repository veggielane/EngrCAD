using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// One extracted iso-contour: an ordered 3D polyline on the mesh surface. A closed loop
/// does not repeat its first point — <see cref="IsClosed"/> says so instead.
/// </summary>
public sealed record MeshIsoCurve(IReadOnlyList<Vector3d> Points, bool IsClosed)
{
    /// <summary>Total polyline length (closing segment included for loops).</summary>
    public double Length
    {
        get
        {
            double sum = 0;
            for (int i = 1; i < Points.Count; i++)
                sum += (Points[i] - Points[i - 1]).Length;
            if (IsClosed && Points.Count > 1)
                sum += (Points[0] - Points[^1]).Length;
            return sum;
        }
    }
}

/// <summary>
/// Iso-contours of a per-vertex scalar field, as ordered polylines (g3's
/// <c>MeshIsoCurves</c>): marching triangles with exact linear interpolation along
/// edges. The crossing on each undirected edge is computed <b>once</b>, from the edge's
/// lower-indexed vertex — the boolean seam lesson: both adjacent triangles then share
/// the endpoint bit-identically, so segments chain by construction rather than by
/// tolerance (the same contract <c>SdfContours</c> documents for its grid cells).
/// Chaining itself is combinatorial (segments meet at mesh edges, and an edge has at
/// most two faces), which is stronger still.
/// </summary>
/// <remarks>
/// <para>
/// Orientation: each polyline runs with the below-level region (value &lt; level) on its
/// LEFT as seen from the mesh's outward side — so on a consistently wound mesh the
/// contours of, say, a height field are consistently wound too.
/// </para>
/// <para>
/// A contour passing exactly through a vertex is the marching-squares node case: the
/// classification is strict (inside = value &lt; level, so a vertex AT the level is
/// outside), zero-length segments are dropped, and chains may legitimately split at
/// such a vertex — the same multiplicity caveat <c>SdfContours</c> carries.
/// </para>
/// </remarks>
public static class MeshIsoCurves
{
    /// <summary>Extracts the contours of <paramref name="vertexValues"/> = <paramref name="level"/>.</summary>
    public static IReadOnlyList<MeshIsoCurve> Extract(HalfEdgeMesh mesh, IReadOnlyList<double> vertexValues, double level)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(vertexValues);
        if (vertexValues.Count != mesh.VertexCount)
            throw new ArgumentException(
                $"Expected one value per vertex ({mesh.VertexCount}), got {vertexValues.Count}.", nameof(vertexValues));

        var triangulated = mesh.Triangulated(); // vertex indices preserved

        // Crossing point per undirected edge (keyed by canonical half-edge index),
        // computed once from the lower-indexed VERTEX so the value depends only on the
        // edge — both faces see identical bits.
        var crossings = new Dictionary<int, Vector3d>();
        Vector3d CrossingOf(HalfEdge h)
        {
            int canonical = Math.Min(h.Index, h.Twin.Index);
            if (crossings.TryGetValue(canonical, out var cached))
                return cached;
            int i = h.Origin.Index, j = h.Destination.Index;
            if (i > j)
                (i, j) = (j, i);
            double vi = vertexValues[i], vj = vertexValues[j];
            // Only called for crossing edges, where the strict inside test guarantees
            // vi != vj; the clamp is a pure round-off guard (the classification already
            // decided the topology) — same rule as SdfContours.Crossing.
            double t = Math.Clamp((level - vi) / (vj - vi), 0.0, 1.0);
            var a = triangulated.GetPosition(i);
            var b = triangulated.GetPosition(j);
            var point = a + (b - a) * t;
            crossings[canonical] = point;
            return point;
        }

        // One directed segment per mixed triangle: from the crossing on the in→out
        // half-edge to the crossing on the out→in half-edge, which puts the below-level
        // region on the segment's left (in the face's own orientation).
        // Keyed by canonical edge indices for combinatorial chaining.
        var segments = new List<(int TailEdge, int HeadEdge, Vector3d Tail, Vector3d Head)>();
        foreach (var face in triangulated.Faces)
        {
            HalfEdge? exit = null, entry = null;
            foreach (var h in face.HalfEdges())
            {
                bool originIn = vertexValues[h.Origin.Index] < level;
                bool destinationIn = vertexValues[h.Destination.Index] < level;
                if (originIn && !destinationIn)
                    exit = h;
                else if (!originIn && destinationIn)
                    entry = h;
            }
            if (exit is null || entry is null)
                continue; // uniform triangle
            var tail = CrossingOf(exit.Value);
            var head = CrossingOf(entry.Value);
            if (tail == head)
                continue; // exact-equality degenerate drop (contour through a vertex)
            segments.Add((
                Math.Min(exit.Value.Index, exit.Value.Twin.Index),
                Math.Min(entry.Value.Index, entry.Value.Twin.Index),
                tail, head));
        }

        // Chain: a segment's head edge is the next segment's tail edge. Each edge has at
        // most two faces, so each edge key appears at most once as a head and once as a
        // tail — successors are unique.
        var successorByTail = new Dictionary<int, int>(); // tail edge key -> segment index
        var predecessorByHead = new Dictionary<int, int>(); // head edge key -> segment index
        for (int s = 0; s < segments.Count; s++)
        {
            // A duplicate key would need three faces on an edge, which Build forbids.
            successorByTail.Add(segments[s].TailEdge, s);
            predecessorByHead.Add(segments[s].HeadEdge, s);
        }

        var used = new bool[segments.Count];
        var curves = new List<MeshIsoCurve>();
        for (int start = 0; start < segments.Count; start++)
        {
            if (used[start])
                continue;

            // Walk backwards first so open chains start at their true beginning
            // (a closed loop's back walk cycles; the seen-set stops it).
            int first = start;
            var seenBackwards = new HashSet<int> { start };
            while (predecessorByHead.TryGetValue(segments[first].TailEdge, out int previous)
                   && !used[previous] && seenBackwards.Add(previous))
            {
                first = previous;
            }

            var points = new List<Vector3d> { segments[first].Tail };
            bool closed = false;
            int current = first;
            while (true)
            {
                used[current] = true;
                points.Add(segments[current].Head);
                if (!successorByTail.TryGetValue(segments[current].HeadEdge, out int next) || used[next])
                {
                    closed = segments[current].HeadEdge == segments[first].TailEdge;
                    break;
                }
                current = next;
            }
            if (closed)
                points.RemoveAt(points.Count - 1); // the walk re-added the start point
            curves.Add(new MeshIsoCurve(points, closed));
        }
        return curves;
    }

    /// <summary>Convenience overload evaluating <paramref name="field"/> at every vertex.</summary>
    public static IReadOnlyList<MeshIsoCurve> Extract(HalfEdgeMesh mesh, Func<Vector3d, double> field, double level)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(field);
        var values = new double[mesh.VertexCount];
        for (int v = 0; v < values.Length; v++)
            values[v] = field(mesh.GetPosition(v));
        return Extract(mesh, values, level);
    }
}
