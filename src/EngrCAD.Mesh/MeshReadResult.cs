using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// What a mesh reader found wrong with (or dropped from) a file. Real-world STL/OBJ
/// files are frequently dirty; these counts describe the welded indexed soup so the
/// repair pipeline (<see cref="MeshRepair"/>) or the caller can decide what to do.
/// </summary>
public sealed class MeshReadDiagnostics
{
    /// <summary>Faces dropped during welding because they collapsed (fewer than three
    /// distinct vertices, or a pinching repeated vertex).</summary>
    public int DegenerateFacesDropped { get; init; }

    /// <summary>Extra copies of faces sharing an identical vertex cycle (either
    /// winding). Counted, not removed — <see cref="MeshRepair.Clean(MeshReadResult, MeshRepairOptions?)"/> removes them.</summary>
    public int DuplicateFaces { get; init; }

    /// <summary>Undirected edges used by more than two faces (fins, bow-tie sheets).</summary>
    public int NonManifoldEdges { get; init; }

    /// <summary>Undirected edges used by exactly two faces in the same direction — the
    /// signature of a flipped patch.</summary>
    public int InconsistentlyWoundEdges { get; init; }

    /// <summary>Directed edges with no reverse partner: open boundary (cracks, holes).</summary>
    public int BoundaryEdges { get; init; }

    /// <summary>Parser notes: skipped constructs, truncation, format oddities.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The <see cref="HalfEdgeMesh.Build"/> failure message when the soup did
    /// not assemble into a manifold mesh; null when it did.</summary>
    public string? BuildError { get; init; }

    /// <summary>True when nothing suspicious was found in the soup itself (the file may
    /// still have produced parser warnings).</summary>
    public bool IsCleanTopology =>
        NonManifoldEdges == 0 && InconsistentlyWoundEdges == 0 && DuplicateFaces == 0;

    public override string ToString() =>
        $"non-manifold edges: {NonManifoldEdges}, inconsistently wound edges: {InconsistentlyWoundEdges}, " +
        $"boundary edges: {BoundaryEdges}, duplicate faces: {DuplicateFaces}, " +
        $"degenerate faces dropped: {DegenerateFacesDropped}, warnings: {Warnings.Count}" +
        (BuildError is null ? "" : $", build error: {BuildError}");
}

/// <summary>
/// The result of reading a mesh file: always the welded indexed soup
/// (<see cref="Positions"/> + <see cref="Faces"/>) plus <see cref="Diagnostics"/>,
/// and — when the soup passed <see cref="HalfEdgeMesh.Build"/>'s manifold
/// validation — the built <see cref="Mesh"/>. Dirty files (non-manifold fins,
/// flipped patches, cracks) leave <see cref="Mesh"/> null so the repair pipeline
/// (<see cref="MeshRepair.Clean(MeshReadResult, MeshRepairOptions?)"/>) can take
/// over from the soup.
/// </summary>
public sealed class MeshReadResult
{
    /// <summary>Welded vertex positions — exact coordinates from the file (welding
    /// maps coincident vertices to one representative; it never moves points).</summary>
    public IReadOnlyList<Vector3d> Positions { get; }

    /// <summary>Face loops as indices into <see cref="Positions"/>. The STL/OBJ/OFF
    /// readers emit triangles (polygonal OBJ/OFF faces are triangulated on read).</summary>
    public IReadOnlyList<int[]> Faces { get; }

    /// <summary>The manifold half-edge mesh, or null when the soup failed manifold
    /// validation (see <see cref="MeshReadDiagnostics.BuildError"/>).</summary>
    public HalfEdgeMesh? Mesh { get; }

    public MeshReadDiagnostics Diagnostics { get; }

    /// <summary>True when the file assembled directly into a manifold mesh.</summary>
    public bool IsManifold => Mesh is not null;

    private MeshReadResult(
        IReadOnlyList<Vector3d> positions, IReadOnlyList<int[]> faces,
        HalfEdgeMesh? mesh, MeshReadDiagnostics diagnostics)
    {
        Positions = positions;
        Faces = faces;
        Mesh = mesh;
        Diagnostics = diagnostics;
    }

    /// <summary>Returns the manifold mesh or throws with the diagnostics summary,
    /// pointing at the repair pipeline.</summary>
    public HalfEdgeMesh RequireMesh() =>
        Mesh ?? throw new InvalidOperationException(
            $"The file did not produce a manifold mesh ({Diagnostics}). " +
            "Use MeshRepair.Clean(result) to run the repair pipeline on the soup.");

    /// <summary>
    /// Finalizes a welded soup into a read result: compacts unreferenced vertices,
    /// computes edge/duplicate diagnostics, and attempts the manifold
    /// <see cref="HalfEdgeMesh.Build"/> (failure is recorded, not thrown).
    /// </summary>
    internal static MeshReadResult Create(
        List<Vector3d> positions, List<int[]> faces, List<string> warnings, int degenerateDropped)
    {
        var (compactPositions, compactFaces, droppedVertices) = MeshSoupOps.Compact(positions, faces);
        if (droppedVertices > 0)
            warnings.Add($"{droppedVertices} unreferenced vertices dropped.");

        var (nonManifold, inconsistent, boundary) = EdgeStatistics(compactFaces);
        int duplicates = CountDuplicateFaces(compactFaces);

        HalfEdgeMesh? mesh = null;
        string? buildError = null;
        try
        {
            mesh = HalfEdgeMesh.Build(compactPositions, compactFaces);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            buildError = e.Message;
        }

        return new MeshReadResult(compactPositions, compactFaces, mesh, new MeshReadDiagnostics
        {
            DegenerateFacesDropped = degenerateDropped,
            DuplicateFaces = duplicates,
            NonManifoldEdges = nonManifold,
            InconsistentlyWoundEdges = inconsistent,
            BoundaryEdges = boundary,
            Warnings = warnings,
            BuildError = buildError,
        });
    }

    /// <summary>Undirected edge usage statistics over an indexed soup. Tuple keys, not
    /// packed longs — the decimator's hashing lesson.</summary>
    internal static (int NonManifold, int InconsistentlyWound, int Boundary) EdgeStatistics(
        IReadOnlyList<int[]> faces)
    {
        var edges = new Dictionary<(int, int), (int Forward, int Backward)>();
        foreach (var face in faces)
        {
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i];
                int b = face[(i + 1) % face.Length];
                bool forward = a < b;
                var key = forward ? (a, b) : (b, a);
                edges.TryGetValue(key, out var uses);
                edges[key] = forward ? (uses.Forward + 1, uses.Backward) : (uses.Forward, uses.Backward + 1);
            }
        }

        int nonManifold = 0, inconsistent = 0, boundary = 0;
        foreach (var (forward, backward) in edges.Values)
        {
            int total = forward + backward;
            if (total == 1)
                boundary++;
            else if (total > 2)
                nonManifold++;
            else if (forward == 2 || backward == 2)
                inconsistent++;
        }
        return (nonManifold, inconsistent, boundary);
    }

    /// <summary>Counts extra copies of faces sharing a canonical vertex cycle.</summary>
    internal static int CountDuplicateFaces(IReadOnlyList<int[]> faces)
    {
        var seen = new HashSet<string>();
        int duplicates = 0;
        foreach (var face in faces)
        {
            if (!seen.Add(MeshSoupOps.CanonicalFaceKey(face)))
                duplicates++;
        }
        return duplicates;
    }
}
