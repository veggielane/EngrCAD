using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// Direct mesh construction for <see cref="TwistExtrudeShape"/>: the sketch's boundary
/// polygons (outer + holes, flattened at a quality-derived chord tolerance) are swept
/// through <c>slices</c> section rings — each ring the section transform
/// R(twist·t)·S(t) applied to the same 2D points — with side walls stitched between
/// consecutive rings and the two caps triangulated once by
/// <see cref="PolygonTriangulator.TriangulateWithHoles"/> (whose index layout, the
/// concatenation [outer, holes...], is exactly the ring layout, so caps and walls
/// share vertices by construction and the mesh closes by index, not by tolerance).
/// </summary>
internal static class TwistedExtrusion
{
    public static HalfEdgeMesh Build(TwistExtrudeShape shape, MeshQuality quality)
    {
        var sketch = shape.Sketch;

        // Chord tolerance sized so a full circle of the sketch's own extent flattens
        // to about SegmentsPerCircle chords: sagitta = r·(1 − cos(π/n)) — the same
        // criterion the B-Rep tessellator's circles follow, expressed relatively
        // because a sketch's units are the caller's choice.
        var bounds = sketch.Bounds;
        double radius = Math.Max(bounds.Size.X, bounds.Size.Y) / 2;
        double chord = Math.Max(radius * (1 - Math.Cos(Math.PI / Math.Max(8, quality.SegmentsPerCircle))), 1e-12);

        return BuildFromLoops(shape, quality, chord);
    }

    private static HalfEdgeMesh BuildFromLoops(TwistExtrudeShape shape, MeshQuality quality, double chord)
    {
        var sketch = shape.Sketch;
        var outer = FlattenLoop(sketch, chord);
        var holes = new List<Vector2d[]>(sketch.Holes.Count);
        foreach (var hole in sketch.Holes)
            holes.Add(FlattenLoop(hole, chord));

        // A twisted sweep needs the PROFILE subdivided, not just the height: a
        // boundary point at radius r moves r·dTheta sideways between rings, and a wall
        // panel spanning a long straight edge lerps that lateral motion linearly along
        // the edge — cutting O(dTheta·L) of area at mid-slab, which makes the volume
        // converge only FIRST order in the slice count (measured: deficit 286 → 33 →
        // 8.2 for slices 8 → 64 → 256 on a twisted square). Subdividing edges to the
        // spacing a circle of the profile's own radius gets go makes the panel error
        // the usual sagitta and restores second-order convergence.
        if (shape.IsTwisted)
        {
            double radius = 0;
            foreach (var p in outer)
                radius = Math.Max(radius, p.Length);
            double maxEdge = 2 * Math.PI * Math.Max(radius, 1e-12) / Math.Max(8, quality.SegmentsPerCircle);
            outer = Subdivide(outer, maxEdge);
            for (int i = 0; i < holes.Count; i++)
                holes[i] = Subdivide(holes[i], maxEdge);
        }

        // Ring layout: [outer, holes...] — the same concatenation the cap
        // triangulation indexes into.
        var section = new List<Vector2d>(outer.Length + holes.Sum(h => h.Length));
        var loopStarts = new List<int> { 0 };
        section.AddRange(outer);
        foreach (var hole in holes)
        {
            loopStarts.Add(section.Count);
            section.AddRange(hole);
        }
        int ringSize = section.Count;

        int slices = shape.Slices ?? DefaultSlices(shape.Twist, quality);
        int ringCount = slices + 1;

        // Vertices: ring r at height fraction t = r/slices, section transform baked.
        var positions = new List<Vector3d>(ringCount * ringSize);
        for (int r = 0; r < ringCount; r++)
        {
            double t = (double)r / slices;
            double angle = shape.Twist * t;
            double sx = 1 + (shape.ScaleTop.X - 1) * t;
            double sy = 1 + (shape.ScaleTop.Y - 1) * t;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double z = shape.Height * t;
            foreach (var p in section)
            {
                double x = p.X * sx, y = p.Y * sy;                   // scale about the plane origin...
                var local = new Vector3d(x * cos - y * sin, x * sin + y * cos, z); // ...then twist
                positions.Add(shape.PlaneMatrix.TransformPoint(local));
            }
        }

        var faces = new List<IReadOnlyList<int>>();

        // Side walls: quads between consecutive rings, split along a diagonal (the
        // twisted quad is non-planar). Outer loops are CCW (Sketch normalizes), so
        // (bottom j, bottom j+1, top j+1, top j) faces outward; a hole loop bounds
        // material from the other side, so its winding flips.
        for (int loop = 0; loop < loopStarts.Count; loop++)
        {
            int start = loopStarts[loop];
            int count = (loop + 1 < loopStarts.Count ? loopStarts[loop + 1] : ringSize) - start;
            bool isHole = loop > 0;
            for (int r = 0; r < slices; r++)
            {
                int bottom = r * ringSize + start;
                int top = bottom + ringSize;
                for (int j = 0; j < count; j++)
                {
                    int j1 = (j + 1) % count;
                    int b0 = bottom + j, b1 = bottom + j1, t1 = top + j1, t0 = top + j;
                    if (isHole)
                    {
                        faces.Add([b1, b0, t0]);
                        faces.Add([b1, t0, t1]);
                    }
                    else
                    {
                        faces.Add([b0, b1, t1]);
                        faces.Add([b0, t1, t0]);
                    }
                }
            }
        }

        // Caps: one triangulation of the section, reused for both rings. Indices refer
        // to [outer, holes...] — the ring layout. Triangles come back CCW in 2D, which
        // is the outward (+normal) winding for the TOP cap; the bottom cap reverses.
        // Earcut filters the EXACTLY collinear vertices the twist subdivision inserted,
        // while the walls still reference them — so cap edges that chord over a run of
        // dropped vertices are expanded back into the full run (the collinear-chord-zip
        // precedent from MeshPlaneCut), and the mesh closes by index.
        var capHoles = new IReadOnlyList<Vector2d>[holes.Count];
        for (int i = 0; i < holes.Count; i++)
            capHoles[i] = holes[i];
        var cap = holes.Count == 0
            ? PolygonTriangulator.Triangulate(outer)
            : PolygonTriangulator.TriangulateWithHoles(outer, capHoles);
        var capFaces = ZipCap(cap, loopStarts, ringSize);
        int topOffset = slices * ringSize;
        foreach (var face in capFaces)
        {
            var bottom = new int[face.Count];
            var top = new int[face.Count];
            for (int i = 0; i < face.Count; i++)
            {
                bottom[i] = face[face.Count - 1 - i];                // reversed: normal -z
                top[i] = topOffset + face[i];                        // as-is: normal +z
            }
            faces.Add(bottom);
            faces.Add(top);
        }

        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>
    /// Expands cap-triangle edges that chord across runs of earcut-dropped (exactly
    /// collinear) vertices back into the full vertex run, per loop of the concatenated
    /// [outer, holes...] layout. Runs are searched in both traversal directions —
    /// earcut walks holes opposite to their stored order.
    /// </summary>
    private static List<IReadOnlyList<int>> ZipCap(
        List<(int A, int B, int C)> triangles, List<int> loopStarts, int ringSize)
    {
        var used = new bool[ringSize];
        foreach (var (a, b, c) in triangles)
            used[a] = used[b] = used[c] = true;

        var result = new List<IReadOnlyList<int>>(triangles.Count);
        bool anyDropped = Array.IndexOf(used, false) >= 0;
        foreach (var (a, b, c) in triangles)
        {
            if (!anyDropped)
            {
                result.Add([a, b, c]);
                continue;
            }
            var polygon = new List<int>(6);
            AppendEdge(a, b);
            AppendEdge(b, c);
            AppendEdge(c, a);
            result.Add(polygon);

            void AppendEdge(int from, int to)
            {
                polygon.Add(from);
                int loop = LoopOf(from);
                if (loop != LoopOf(to))
                    return;                                          // an interior bridge/diagonal
                int start = loopStarts[loop];
                int count = (loop + 1 < loopStarts.Count ? loopStarts[loop + 1] : ringSize) - start;
                if (TryRun(start, count, from - start, to - start, forward: true, out var run) ||
                    TryRun(start, count, from - start, to - start, forward: false, out run))
                    polygon.AddRange(run);
            }

            bool TryRun(int start, int count, int from, int to, bool forward, out List<int> run)
            {
                run = [];
                int gap = forward ? (to - from + count) % count : (from - to + count) % count;
                if (gap <= 1)
                    return false;
                for (int k = 1; k < gap; k++)
                {
                    int index = start + (forward ? (from + k) % count : (from - k + count * count) % count);
                    if (used[index])
                        return false;                                // a live vertex: not a dropped run
                    run.Add(index);
                }
                return true;
            }

            int LoopOf(int index)
            {
                for (int i = loopStarts.Count - 1; i >= 0; i--)
                {
                    if (index >= loopStarts[i])
                        return i;
                }
                return 0;
            }
        }
        return result;
    }

    /// <summary>Splits every boundary edge longer than <paramref name="maxEdge"/> into
    /// equal parts. The inserted points are exactly collinear with their edge, which is
    /// safe on the WALLS (they subdivide the panel) and on the CAPS (the triangulator
    /// keeps them as fat-triangle corners once the twist makes neighbours consume
    /// them; an untwisted call never reaches here).</summary>
    private static Vector2d[] Subdivide(Vector2d[] loop, double maxEdge)
    {
        var result = new List<Vector2d>(loop.Length * 2);
        for (int i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Length];
            result.Add(a);
            int parts = (int)Math.Ceiling(a.DistanceTo(b) / maxEdge);
            for (int k = 1; k < parts; k++)
                result.Add(Vector2d.Lerp(a, b, (double)k / parts));
        }
        return [.. result];
    }

    /// <summary>Ring count between base and top: enough that each ring-to-ring twist
    /// step matches the quality's circular facet angle. A pure taper needs one span.</summary>
    private static int DefaultSlices(double twist, MeshQuality quality)
    {
        if (twist == 0)
            return 1;
        double step = 2 * Math.PI / Math.Max(8, quality.SegmentsPerCircle);
        return Math.Max(2, (int)Math.Ceiling(Math.Abs(twist) / step));
    }

    /// <summary>One sketch boundary as a closed polygon (no duplicated closing point):
    /// each segment flattens its own span including its start, so concatenation closes
    /// exactly; consecutive weld-scale duplicates are dropped.</summary>
    private static Vector2d[] FlattenLoop(Sketch sketch, double chord)
    {
        var points = new List<Vector2d>();
        foreach (var segment in sketch.Segments)
            segment.Flatten(chord, points);
        // Drop consecutive duplicates (and a duplicated wrap point) at the 1e-9 weld
        // tier — exactly-shared segment endpoints, the same guard GlyphOutlines uses.
        var compact = new List<Vector2d>(points.Count);
        foreach (var p in points)
        {
            if (compact.Count > 0 && compact[^1].DistanceTo(p) <= 1e-9)
                continue;
            compact.Add(p);
        }
        while (compact.Count > 1 && compact[0].DistanceTo(compact[^1]) <= 1e-9)
            compact.RemoveAt(compact.Count - 1);

        // Drop EXACTLY collinear interior points (a subdivided straight edge, glyph
        // outlines): PolygonTriangulator filters them from the caps with the same
        // exact-zero test, and a vertex present in a wall but absent from its cap is a
        // T-junction the manifold Build rejects. Filtering here keeps walls and caps
        // agreeing about the vertex set. Exact zero, not a tolerance — near-collinear
        // points survive in both places alike.
        for (int i = compact.Count - 1; i >= 0 && compact.Count > 3; i--)
        {
            var previous = compact[(i + compact.Count - 1) % compact.Count];
            var current = compact[i];
            var next = compact[(i + 1) % compact.Count];
            if ((current - previous).Cross(next - current) == 0)
                compact.RemoveAt(i);
        }
        if (compact.Count < 3)
            throw new InvalidOperationException("The sketch flattens to fewer than 3 boundary points.");
        return [.. compact];
    }
}
