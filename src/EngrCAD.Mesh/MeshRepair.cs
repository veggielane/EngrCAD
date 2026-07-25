using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>Options for <see cref="MeshRepair.Clean(MeshReadResult, MeshRepairOptions?)"/>.</summary>
public sealed record MeshRepairOptions
{
    /// <summary>
    /// Crack-closing weld distance. Defaults to the 1e-7 seam tier of the epsilon
    /// ladder — cracks come from geometry authored independently on two sides of a
    /// seam, exactly the case that tier covers. Note binary STL input is
    /// float32-quantized: cracks below one float ulp (~1.2e-7 at coordinate 1.0)
    /// cannot exist in such files, so closing real STL cracks may need a looser
    /// value. Repair welds by design — unlike the readers' 1e-9 default, this can
    /// merge genuinely distinct vertices on models with sub-tolerance features.
    /// </summary>
    public double WeldTolerance { get; init; } = 1e-7;

    /// <summary>Remove all but one copy of faces sharing a vertex cycle (either
    /// winding); the orientation pass then fixes the survivor's winding.</summary>
    public bool RemoveDuplicateFaces { get; init; } = true;

    /// <summary>Drop faces thinner than <see cref="WeldTolerance"/> (minimum triangle
    /// altitude below the weld distance — such faces are numerically weldable noise).</summary>
    public bool RemoveDegenerateFaces { get; init; } = true;

    /// <summary>Re-wind faces consistently per connected component (BFS flood), then
    /// flip whole components to face outward (signed volume for closed components,
    /// winding-number probes for open ones).</summary>
    public bool OrientFaces { get; init; } = true;

    /// <summary>Insert T-junction vertices into unmatched edges they lie on
    /// (<see cref="MeshWelder"/>'s seam zip), closing cracks where one side is
    /// subdivided finer than the other.</summary>
    public bool ZipTJunctions { get; init; } = true;

    /// <summary>
    /// Distance within which <see cref="MeshRepair.AutoRepair(MeshReadResult, MeshRepairOptions?)"/>
    /// welds leftover coincident boundary edge pairs
    /// (<see cref="MeshRepair.MergeCoincidentEdges"/>). Defaults to
    /// <see cref="WeldTolerance"/>. Because every merge is guarded, this can be loosened
    /// far beyond a safe vertex-weld distance: an unsafe merge is refused, not applied.
    /// </summary>
    public double? CrackMergeTolerance { get; init; }

    /// <summary>Options for the hole-filling stage of
    /// <see cref="MeshRepair.AutoRepair(MeshReadResult, MeshRepairOptions?)"/>.</summary>
    public HoleFillOptions HoleFill { get; init; } = HoleFillOptions.Default;
}

/// <summary>What <see cref="MeshRepair.Clean(MeshReadResult, MeshRepairOptions?)"/> did.</summary>
public sealed class MeshRepairReport
{
    /// <summary>Vertices merged into a coincident representative by the crack weld.</summary>
    public int VerticesMerged { get; init; }

    /// <summary>Extra copies of duplicated faces removed.</summary>
    public int DuplicateFacesRemoved { get; init; }

    /// <summary>Faces dropped as degenerate (collapsed by welding, pinched, or thinner
    /// than the weld tolerance).</summary>
    public int DegenerateFacesRemoved { get; init; }

    /// <summary>Connected components found (via shared two-use edges).</summary>
    public int ComponentCount { get; init; }

    /// <summary>Individual faces re-wound by the BFS consistency flood.</summary>
    public int FacesRewound { get; init; }

    /// <summary>Whole components flipped by the outward vote.</summary>
    public int ComponentsFlipped { get; init; }

    /// <summary>Vertices inserted into unmatched edges by the T-junction zip.</summary>
    public int TJunctionVerticesInserted { get; init; }

    /// <summary>Coincident boundary edge pairs welded by the crack merge (AutoRepair only).</summary>
    public int CracksMerged { get; init; }

    /// <summary>Boundary loops closed by the hole filler (AutoRepair only).</summary>
    public int HolesFilled { get; init; }

    /// <summary>Boundary loops the hole filler declined to close (AutoRepair only); see <see cref="Notes"/>.</summary>
    public int HolesSkipped { get; init; }

    /// <summary>True when the repaired mesh is closed (watertight).</summary>
    public bool IsClosed { get; init; }

    /// <summary>Anything the pipeline could not decide (e.g. an open component with no
    /// orientation evidence).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public override string ToString() =>
        $"merged {VerticesMerged} vertices, removed {DuplicateFacesRemoved} duplicate + " +
        $"{DegenerateFacesRemoved} degenerate faces, rewound {FacesRewound} faces, " +
        $"flipped {ComponentsFlipped} of {ComponentCount} components, " +
        $"inserted {TJunctionVerticesInserted} T-junction vertices" +
        (CracksMerged > 0 ? $", welded {CracksMerged} crack edges" : "") +
        (HolesFilled > 0 || HolesSkipped > 0 ? $", filled {HolesFilled} of {HolesFilled + HolesSkipped} holes" : "") +
        "; " +
        (IsClosed ? "result is closed" : "result has open boundaries") +
        (Notes.Count > 0 ? $"; notes: {string.Join(" | ", Notes)}" : "");

    internal MeshRepairReport With(bool isClosed, int cracksMerged, int holesFilled, int holesSkipped, IReadOnlyList<string> notes) =>
        new()
        {
            VerticesMerged = VerticesMerged,
            DuplicateFacesRemoved = DuplicateFacesRemoved,
            DegenerateFacesRemoved = DegenerateFacesRemoved,
            ComponentCount = ComponentCount,
            FacesRewound = FacesRewound,
            ComponentsFlipped = ComponentsFlipped,
            TJunctionVerticesInserted = TJunctionVerticesInserted,
            CracksMerged = cracksMerged,
            HolesFilled = holesFilled,
            HolesSkipped = holesSkipped,
            IsClosed = isClosed,
            Notes = notes,
        };
}

/// <summary>
/// Repair pipeline v1 for dirty mesh soups (typically from <see cref="MeshReader"/>):
/// weld cracks → drop degenerate faces → remove duplicate faces → orient windings
/// (BFS flood per component + outward vote) → zip T-junction seams → manifold
/// <see cref="HalfEdgeMesh.Build"/>. Construct-new style: inputs are never mutated.
///
/// The outward vote uses the generalized winding number
/// (<see cref="MeshWindingNumber.TriangleSolidAngle"/>): for a closed component the
/// component's signed volume — the same integral — decides exactly; open components
/// probe just inside their largest faces and vote by the winding sign there.
/// Limitations (v1, by design): nested cavity shells are oriented outward like
/// everything else (a hollow part gains its cavity's volume), and defects needing
/// topological surgery (fins, holes to fill) are out of scope until the mutable
/// Euler operators land — such soups still fail the final Build, loudly.
/// </summary>
public static partial class MeshRepair
{
    /// <summary>Runs the pipeline on a reader result's welded soup.</summary>
    public static (HalfEdgeMesh Mesh, MeshRepairReport Report) Clean(
        MeshReadResult soup, MeshRepairOptions? options = null) =>
        Clean(soup.Positions, soup.Faces, options);

    /// <summary>
    /// <see cref="Clean(MeshReadResult, MeshRepairOptions?)"/> plus the topological
    /// surgery that needs a valid mesh to stand on: leftover cracks are welded pair-wise
    /// (<see cref="MergeCoincidentEdges"/>) and whatever boundary loops remain go to
    /// <see cref="HoleFiller.FillAll"/>. The full "make this printable" dispatch — the
    /// cheap, always-safe passes first, surgery only where the geometry still asks for it.
    /// Returns early (doing nothing extra) when <c>Clean</c> already produced a closed
    /// mesh, so the common case costs nothing.
    /// </summary>
    public static (HalfEdgeMesh Mesh, MeshRepairReport Report) AutoRepair(
        MeshReadResult soup, MeshRepairOptions? options = null) =>
        AutoRepair(soup.Positions, soup.Faces, options);

    /// <summary>Full repair of an existing mesh; see <see cref="AutoRepair(MeshReadResult, MeshRepairOptions?)"/>.</summary>
    public static (HalfEdgeMesh Mesh, MeshRepairReport Report) AutoRepair(
        HalfEdgeMesh mesh, MeshRepairOptions? options = null)
    {
        var (positions, faces) = mesh.ToIndexed();
        return AutoRepair(positions, faces, options);
    }

    /// <summary>Full repair of a raw indexed soup; see <see cref="AutoRepair(MeshReadResult, MeshRepairOptions?)"/>.</summary>
    public static (HalfEdgeMesh Mesh, MeshRepairReport Report) AutoRepair(
        IReadOnlyList<Vector3d> positions, IReadOnlyList<int[]> faces, MeshRepairOptions? options = null)
    {
        var opt = options ?? new MeshRepairOptions();
        var (mesh, report) = Clean(positions, faces, opt);
        if (mesh.IsClosed)
            return (mesh, report);

        var notes = new List<string>(report.Notes);

        // 1. Weld coincident boundary edge pairs. Guarded, so a merge that would pinch the
        //    surface is refused rather than applied; positions never move.
        int merged = 0;
        var editable = EditableMesh.FromMesh(mesh);
        merged = MergeCoincidentEdges(editable, opt.CrackMergeTolerance ?? opt.WeldTolerance);
        if (merged > 0)
            mesh = editable.ToMesh();

        // 2. Whatever is still open is a genuine hole, not a crack.
        int filled = 0, skipped = 0;
        if (!mesh.IsClosed)
        {
            var result = HoleFiller.FillAll(mesh, opt.HoleFill);
            mesh = result.Mesh;
            foreach (var outcome in result.Outcomes)
            {
                if (outcome.Method == HoleFillMethod.Skipped)
                {
                    skipped++;
                    notes.Add($"Hole {outcome.LoopIndex} ({outcome.VertexCount} vertices) left open: {outcome.Message}");
                }
                else
                {
                    filled++;
                }
            }
        }

        return (mesh, report.With(mesh.IsClosed, merged, filled, skipped, notes));
    }

    /// <summary>Runs the pipeline on an existing mesh (e.g. to re-orient an
    /// inward-wound but manifold import). Faces may be polygons; they are preserved.</summary>
    public static (HalfEdgeMesh Mesh, MeshRepairReport Report) Clean(
        HalfEdgeMesh mesh, MeshRepairOptions? options = null)
    {
        var (positions, faces) = mesh.ToIndexed();
        return Clean(positions, faces, options);
    }

    /// <summary>Runs the pipeline on a raw indexed soup.</summary>
    public static (HalfEdgeMesh Mesh, MeshRepairReport Report) Clean(
        IReadOnlyList<Vector3d> positions, IReadOnlyList<int[]> faces, MeshRepairOptions? options = null)
    {
        var opt = options ?? new MeshRepairOptions();
        var notes = new List<string>();

        // 1. Weld: merge vertices within the crack tolerance. Representatives keep
        //    their exact coordinates — welding never moves points.
        var welder = new MeshSoupOps.VertexWelder(opt.WeldTolerance);
        var remap = new int[positions.Count];
        for (int i = 0; i < positions.Count; i++)
            remap[i] = welder.Add(positions[i]);
        int verticesMerged = positions.Count - welder.Points.Count;
        var points = welder.Points;

        int degenerateRemoved = 0;
        var loops = new List<int[]>(faces.Count);
        foreach (var face in faces)
        {
            var mapped = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
                mapped[i] = remap[face[i]];
            var loop = MeshSoupOps.CleanLoop(mapped);
            if (loop is null)
                degenerateRemoved++;
            else
                loops.Add(loop);
        }

        // 2. Degenerate faces: thinner than the weld distance = numerically weldable
        //    noise (triangle: minimum altitude; polygon: Newell area vs perimeter).
        if (opt.RemoveDegenerateFaces)
        {
            int kept = 0;
            for (int f = 0; f < loops.Count; f++)
            {
                if (IsDegenerate(loops[f], points, opt.WeldTolerance))
                    degenerateRemoved++;
                else
                    loops[kept++] = loops[f];
            }
            loops.RemoveRange(kept, loops.Count - kept);
        }

        // 3. Duplicate faces: keep the first copy per canonical vertex cycle (either
        //    winding — the orientation pass fixes the survivor). Must run before the
        //    BFS flood: duplicates overload their edges past two uses, which would
        //    block flood crossings and fragment components.
        int duplicatesRemoved = 0;
        if (opt.RemoveDuplicateFaces)
        {
            var seen = new HashSet<string>();
            int kept = 0;
            for (int f = 0; f < loops.Count; f++)
            {
                if (seen.Add(MeshSoupOps.CanonicalFaceKey(loops[f])))
                    loops[kept++] = loops[f];
                else
                    duplicatesRemoved++;
            }
            loops.RemoveRange(kept, loops.Count - kept);
        }

        // 4. Orientation.
        int componentCount = 0, facesRewound = 0, componentsFlipped = 0;
        if (opt.OrientFaces)
            (componentCount, facesRewound, componentsFlipped) = Orient(loops, points, notes);

        // 5. T-junction zip (MeshWelder's seam pass; inserts crack vertices into the
        //    coarse edges they lie on so both sides carry identical subdivisions).
        int zipInserted = 0;
        if (opt.ZipTJunctions)
        {
            var zipFaces = loops.Select(l => new List<int>(l)).ToList();
            int before = zipFaces.Sum(f => f.Count);
            MeshWelder.ZipSeams(points, zipFaces);
            zipInserted = zipFaces.Sum(f => f.Count) - before;
            if (zipInserted > 0)
            {
                for (int f = 0; f < loops.Count; f++)
                    loops[f] = [.. zipFaces[f]];
            }
        }

        // 6. Assemble.
        var (compactPositions, compactFaces, _) = MeshSoupOps.Compact(points, loops);
        HalfEdgeMesh mesh;
        try
        {
            mesh = HalfEdgeMesh.Build(compactPositions, compactFaces);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            var (nonManifold, inconsistent, boundary) = MeshReadResult.EdgeStatistics(compactFaces);
            throw new InvalidOperationException(
                $"MeshRepair could not produce a manifold mesh: {e.Message} " +
                $"(after repair: non-manifold edges: {nonManifold}, inconsistently wound edges: {inconsistent}, " +
                $"boundary edges: {boundary}). Defects needing topological surgery (fins, hole filling) " +
                "are beyond repair v1.", e);
        }

        var report = new MeshRepairReport
        {
            VerticesMerged = verticesMerged,
            DuplicateFacesRemoved = duplicatesRemoved,
            DegenerateFacesRemoved = degenerateRemoved,
            ComponentCount = componentCount,
            FacesRewound = facesRewound,
            ComponentsFlipped = componentsFlipped,
            TJunctionVerticesInserted = zipInserted,
            IsClosed = mesh.IsClosed,
            Notes = notes,
        };
        return (mesh, report);
    }

    private static bool IsDegenerate(int[] loop, IReadOnlyList<Vector3d> points, double weldTolerance)
    {
        if (loop.Length == 3)
        {
            var a = points[loop[0]];
            var b = points[loop[1]];
            var c = points[loop[2]];
            double doubleArea = (b - a).Cross(c - a).Length;
            double longestEdge = Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));
            // Guard against exact-zero division; a zero-extent triangle is degenerate by definition.
            if (longestEdge == 0)
                return true;
            return doubleArea / longestEdge < weldTolerance; // minimum altitude below the weld distance
        }

        // Polygon: Newell area against perimeter — same "thinner than the weld
        // distance" criterion (area < half tolerance * perimeter).
        double nx = 0, ny = 0, nz = 0, perimeter = 0;
        for (int i = 0; i < loop.Length; i++)
        {
            var p = points[loop[i]];
            var q = points[loop[(i + 1) % loop.Length]];
            nx += (p.Y - q.Y) * (p.Z + q.Z);
            ny += (p.Z - q.Z) * (p.X + q.X);
            nz += (p.X - q.X) * (p.Y + q.Y);
            perimeter += (q - p).Length;
        }
        double area = 0.5 * new Vector3d(nx, ny, nz).Length;
        return area < 0.5 * weldTolerance * perimeter;
    }

    /// <summary>
    /// BFS flood per connected component (crossing only clean two-use edges, so fins
    /// don't propagate bad orientation), re-winding neighbors that traverse a shared
    /// edge in the same direction; then a per-component outward vote.
    /// </summary>
    private static (int Components, int FacesRewound, int ComponentsFlipped) Orient(
        List<int[]> loops, IReadOnlyList<Vector3d> points, List<string> notes)
    {
        // Undirected edge → incident faces (tuple keys — the decimator's hashing lesson).
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < loops.Count; f++)
        {
            var loop = loops[f];
            for (int i = 0; i < loop.Length; i++)
            {
                int a = loop[i];
                int b = loop[(i + 1) % loop.Length];
                var key = a < b ? (a, b) : (b, a);
                if (!edgeFaces.TryGetValue(key, out var incident))
                    edgeFaces[key] = incident = [];
                incident.Add(f);
            }
        }

        var visited = new bool[loops.Count];
        var stack = new Stack<int>();
        var component = new List<int>();
        int components = 0, facesRewound = 0, componentsFlipped = 0;

        for (int seed = 0; seed < loops.Count; seed++)
        {
            if (visited[seed])
                continue;
            components++;
            component.Clear();
            component.Add(seed);
            visited[seed] = true;
            stack.Push(seed);

            bool closed = true;
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                var loop = loops[current];
                for (int i = 0; i < loop.Length; i++)
                {
                    int a = loop[i];
                    int b = loop[(i + 1) % loop.Length];
                    var key = a < b ? (a, b) : (b, a);
                    var incident = edgeFaces[key];
                    if (incident.Count != 2)
                    {
                        closed = false; // boundary or fin
                        continue;
                    }
                    int other = incident[0] == current ? incident[1] : incident[0];
                    if (visited[other])
                        continue;
                    // Consistent orientation means the neighbor traverses (b, a).
                    if (HasDirectedEdge(loops[other], a, b))
                    {
                        Array.Reverse(loops[other]);
                        facesRewound++;
                    }
                    visited[other] = true;
                    component.Add(other);
                    stack.Push(other);
                }
            }

            if (ShouldFlipComponent(component, loops, points, closed, components, notes))
            {
                foreach (int f in component)
                    Array.Reverse(loops[f]);
                componentsFlipped++;
            }
        }
        return (components, facesRewound, componentsFlipped);
    }

    private static bool HasDirectedEdge(int[] loop, int a, int b)
    {
        for (int i = 0; i < loop.Length; i++)
        {
            if (loop[i] == a && loop[(i + 1) % loop.Length] == b)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The outward vote. Closed components: the signed volume decides exactly (it is
    /// the winding-number integral in closed form — negative means inward-wound).
    /// Open components: probe just inside the largest faces and read the sign of the
    /// component's generalized winding number there (|w| ≥ 0.25 counts as evidence).
    /// </summary>
    private static bool ShouldFlipComponent(
        List<int> component, List<int[]> loops, IReadOnlyList<Vector3d> points,
        bool closed, int componentNumber, List<string> notes)
    {
        if (closed)
        {
            double volume = 0;
            foreach (int f in component)
            {
                var loop = loops[f];
                var a = points[loop[0]];
                for (int i = 1; i + 1 < loop.Length; i++)
                    volume += a.Dot(points[loop[i]].Cross(points[loop[i + 1]]));
            }
            return volume < 0;
        }

        // Open component: winding probes at the largest faces.
        var byArea = component
            .Select(f =>
            {
                var loop = loops[f];
                var a = points[loop[0]];
                var areaNormal = Vector3d.Zero;
                for (int i = 1; i + 1 < loop.Length; i++)
                    areaNormal += (points[loop[i]] - a).Cross(points[loop[i + 1]] - a);
                return (Face: f, AreaNormal: areaNormal * 0.5);
            })
            .OrderByDescending(x => x.AreaNormal.Length)
            .Take(8);

        double vote = 0;
        bool evidence = false;
        foreach (var (f, areaNormal) in byArea)
        {
            double area = areaNormal.Length;
            if (!areaNormal.TryNormalize(Tolerance.Default, out var normal))
                continue;
            var loop = loops[f];
            var centroid = Vector3d.Zero;
            foreach (int v in loop)
                centroid += points[v];
            centroid /= loop.Length;

            // Probe just off BOTH sides of the face — 1% of its characteristic size
            // keeps the interior-side probe inside even thin parts. Only one side is
            // interior, and we don't know which: the winding is ~0 on the exterior
            // side regardless of the component's orientation, so a single behind-the-
            // normal probe would be blind to inward-wound sheets. The decision is the
            // SIGN of the winding wherever |w| is substantial — a negative winding
            // means the component wraps that point clockwise, i.e. inward.
            double h = 0.01 * Math.Sqrt(area);
            foreach (double side in (ReadOnlySpan<double>)[-1.0, +1.0])
            {
                var probe = centroid + normal * (side * h);
                double winding = ComponentWinding(component, loops, points, probe);
                if (Math.Abs(winding) < 0.25)
                    continue; // exterior side, or the probe fell through a hole
                evidence = true;
                vote += winding * area;
            }
        }

        if (!evidence)
        {
            notes.Add($"Component {componentNumber}: no orientation evidence (open sheet); left as found.");
            return false;
        }
        return vote < 0;
    }

    private static double ComponentWinding(
        List<int> component, List<int[]> loops, IReadOnlyList<Vector3d> points, in Vector3d probe)
    {
        double sum = 0;
        foreach (int f in component)
        {
            var loop = loops[f];
            var a = points[loop[0]];
            for (int i = 1; i + 1 < loop.Length; i++)
                sum += MeshWindingNumber.TriangleSolidAngle(a, points[loop[i]], points[loop[i + 1]], probe);
        }
        return sum / (4.0 * Math.PI);
    }
}
