using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Wavefront OBJ import — the inverse of <see cref="ObjWriter"/>. Parses `v` and `f`
/// records (all index forms: `v`, `v/vt`, `v//vn`, `v/vt/vn`, and negative relative
/// indices; backslash line continuations honored). Polygonal faces are triangulated
/// in their Newell plane via <see cref="PolygonTriangulator"/> (fan fallback keeps
/// every vertex referenced when earcut filters collinear ones). Texture coordinates,
/// normals, materials, groups, and all non-polygonal constructs are ignored and
/// reported in <see cref="MeshReadDiagnostics.Warnings"/>. Coincident vertices are
/// welded (representatives keep exact file coordinates) and the result attempts the
/// manifold <see cref="HalfEdgeMesh.Build"/>; dirty files return the soup +
/// diagnostics (see <see cref="MeshReadResult"/>).
/// </summary>
public static class ObjReader
{
    /// <param name="path">File to read (.obj).</param>
    /// <param name="weldTolerance">Distance under which vertices weld to one
    /// representative. Defaults to the 1e-9 absolute weld tier
    /// (<c>Tolerance.Default.Linear</c>); OBJ text written with round-trip formatting
    /// (as <see cref="ObjWriter"/> does) reproduces doubles exactly, so this only
    /// merges genuinely duplicated vertices. Crack closing at looser tolerances is
    /// <see cref="MeshRepair"/>'s job.</param>
    public static MeshReadResult ReadFile(string path, double weldTolerance = 1e-9)
    {
        using var reader = new StreamReader(path);
        return Read(reader, weldTolerance);
    }

    /// <summary>Reads an OBJ from a text reader. See <see cref="ReadFile"/> for
    /// parameter semantics.</summary>
    public static MeshReadResult Read(TextReader reader, double weldTolerance = 1e-9)
    {
        var warnings = new List<string>();
        var rawPositions = new List<Vector3d>();
        var rawFaces = new List<int[]>();
        int texCoords = 0, normals = 0, badFaces = 0, badVertices = 0;
        var skipped = new Dictionary<string, int>();
        var culture = CultureInfo.InvariantCulture;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Backslash line continuation (rare but part of the format).
            while (line.EndsWith('\\'))
            {
                string? next = reader.ReadLine();
                if (next is null)
                    break;
                line = line[..^1] + " " + next;
            }

            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            switch (tokens[0])
            {
                case "v":
                    if (tokens.Length >= 4
                        && double.TryParse(tokens[1], NumberStyles.Float, culture, out double x)
                        && double.TryParse(tokens[2], NumberStyles.Float, culture, out double y)
                        && double.TryParse(tokens[3], NumberStyles.Float, culture, out double z))
                    {
                        rawPositions.Add(new Vector3d(x, y, z)); // optional w and vertex colors ignored
                    }
                    else
                    {
                        badVertices++;
                        rawPositions.Add(Vector3d.Zero); // keep indexing consistent
                    }
                    break;
                case "vt":
                    texCoords++;
                    break;
                case "vn":
                    normals++;
                    break;
                case "f":
                {
                    var face = new int[tokens.Length - 1];
                    bool ok = tokens.Length >= 4;
                    for (int i = 1; ok && i < tokens.Length; i++)
                    {
                        if (TryParseFaceIndex(tokens[i], rawPositions.Count, out int v))
                            face[i - 1] = v;
                        else
                            ok = false;
                    }
                    if (ok)
                        rawFaces.Add(face);
                    else
                        badFaces++;
                    break;
                }
                default:
                    skipped.TryGetValue(tokens[0], out int seen);
                    skipped[tokens[0]] = seen + 1;
                    break;
            }
        }

        if (badVertices > 0)
            warnings.Add($"{badVertices} malformed 'v' lines replaced with the origin (indices preserved).");
        if (badFaces > 0)
            warnings.Add($"{badFaces} malformed or out-of-range 'f' lines skipped.");
        if (texCoords > 0 || normals > 0)
            warnings.Add($"Ignored {texCoords} texture coordinates (vt) and {normals} normals (vn) — geometry comes from positions and winding.");
        foreach (var (keyword, n) in skipped)
            warnings.Add($"Skipped construct '{keyword}' ({n} lines).");

        return BuildFromIndexed(rawPositions, rawFaces, weldTolerance, warnings);
    }

    private static bool TryParseFaceIndex(string token, int positionCount, out int index)
    {
        index = -1;
        int slash = token.IndexOf('/');
        var part = slash >= 0 ? token.AsSpan(0, slash) : token.AsSpan();
        if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v == 0)
            return false;
        index = v > 0 ? v - 1 : positionCount + v; // negative = relative to the vertices seen so far
        return index >= 0 && index < positionCount;
    }

    /// <summary>
    /// Shared indexed-file finalization (OBJ and OFF): weld coincident positions,
    /// remap and clean face loops, triangulate polygons, and hand off to
    /// <see cref="MeshReadResult.Create"/>.
    /// </summary>
    internal static MeshReadResult BuildFromIndexed(
        IReadOnlyList<Vector3d> rawPositions, IReadOnlyList<int[]> rawFaces,
        double weldTolerance, List<string> warnings)
    {
        var welder = new MeshSoupOps.VertexWelder(weldTolerance);
        var remap = new int[rawPositions.Count];
        for (int i = 0; i < rawPositions.Count; i++)
            remap[i] = welder.Add(rawPositions[i]);

        var faces = new List<int[]>(rawFaces.Count);
        int degenerate = 0, fanFallbacks = 0;
        foreach (var rawFace in rawFaces)
        {
            var mapped = new int[rawFace.Length];
            for (int i = 0; i < rawFace.Length; i++)
                mapped[i] = remap[rawFace[i]];

            var loop = MeshSoupOps.CleanLoop(mapped);
            if (loop is null)
            {
                degenerate++;
                continue;
            }
            if (loop.Length == 3)
            {
                faces.Add(loop);
                continue;
            }

            var triangles = MeshSoupOps.TriangulatePolygon(loop, welder.Points, out bool fan);
            if (triangles is null)
            {
                degenerate++;
                continue;
            }
            if (fan)
                fanFallbacks++;
            faces.AddRange(triangles);
        }

        if (fanFallbacks > 0)
            warnings.Add($"{fanFallbacks} polygonal faces used fan triangulation (earcut dropped collinear vertices).");
        return MeshReadResult.Create(welder.Points, faces, warnings, degenerate);
    }
}
