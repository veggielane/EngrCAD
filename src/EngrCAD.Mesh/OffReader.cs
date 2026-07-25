using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// OFF (Object File Format) import: `OFF` header (variants like COFF/NOFF accepted
/// with a warning — their extra per-vertex data is ignored), a vertex/face/edge count
/// line, vertex lines (first three numbers), then face lines (`k i0 ... i(k-1)`;
/// trailing color values ignored). Polygonal faces are triangulated like
/// <see cref="ObjReader"/>'s; coincident vertices are welded and the result attempts
/// the manifold <see cref="HalfEdgeMesh.Build"/> (see <see cref="MeshReadResult"/>).
/// </summary>
public static class OffReader
{
    /// <param name="path">File to read (.off).</param>
    /// <param name="weldTolerance">Distance under which vertices weld to one
    /// representative; defaults to the 1e-9 absolute weld tier
    /// (<c>Tolerance.Default.Linear</c>). Crack closing at looser tolerances is
    /// <see cref="MeshRepair"/>'s job.</param>
    public static MeshReadResult ReadFile(string path, double weldTolerance = 1e-9)
    {
        using var reader = new StreamReader(path);
        return Read(reader, weldTolerance);
    }

    /// <summary>Reads an OFF mesh from a text reader. See <see cref="ReadFile"/> for
    /// parameter semantics.</summary>
    public static MeshReadResult Read(TextReader reader, double weldTolerance = 1e-9)
    {
        var warnings = new List<string>();
        var culture = CultureInfo.InvariantCulture;

        var lines = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
                lines.Add(tokens);
        }

        if (lines.Count == 0)
        {
            warnings.Add("Empty OFF file.");
            return MeshReadResult.Create([], [], warnings, 0);
        }

        // Header: "OFF" (counts may share the line) or a known variant; headerless
        // files start directly with the counts.
        int cursor = 0;
        var first = lines[0];
        string[] countTokens;
        if (first[0].EndsWith("OFF", StringComparison.OrdinalIgnoreCase) && !double.TryParse(first[0], NumberStyles.Float, culture, out _))
        {
            if (!first[0].Equals("OFF", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"OFF variant header '{first[0]}' — extra per-vertex attributes will be ignored.");
            cursor = 1;
            countTokens = first.Length > 1 ? first[1..] : (cursor < lines.Count ? lines[cursor++] : []);
        }
        else
        {
            warnings.Add("Missing OFF header; assuming the first line holds the counts.");
            countTokens = lines[cursor++];
        }

        if (countTokens.Length < 2
            || !int.TryParse(countTokens[0], NumberStyles.Integer, culture, out int vertexCount)
            || !int.TryParse(countTokens[1], NumberStyles.Integer, culture, out int faceCount)
            || vertexCount < 0 || faceCount < 0)
        {
            warnings.Add("Could not parse the OFF vertex/face count line; no geometry read.");
            return MeshReadResult.Create([], [], warnings, 0);
        }

        // Header-declared counts are untrusted input: clamp pre-allocation by the lines
        // actually present so an absurd declared count cannot allocate gigabytes (the
        // parse loops below are already bounded by lines.Count).
        var rawPositions = new List<Vector3d>(Math.Min(vertexCount, lines.Count - cursor));
        int badVertices = 0, vertexExtras = 0;
        for (int i = 0; i < vertexCount && cursor < lines.Count; i++, cursor++)
        {
            var tokens = lines[cursor];
            if (tokens.Length >= 3
                && double.TryParse(tokens[0], NumberStyles.Float, culture, out double x)
                && double.TryParse(tokens[1], NumberStyles.Float, culture, out double y)
                && double.TryParse(tokens[2], NumberStyles.Float, culture, out double z))
            {
                rawPositions.Add(new Vector3d(x, y, z));
                if (tokens.Length > 3)
                    vertexExtras++;
            }
            else
            {
                badVertices++;
                rawPositions.Add(Vector3d.Zero); // keep indexing consistent
            }
        }
        if (rawPositions.Count < vertexCount)
            warnings.Add($"OFF declares {vertexCount} vertices but only {rawPositions.Count} lines were present.");

        var rawFaces = new List<int[]>(Math.Min(faceCount, lines.Count - cursor));
        int badFaces = 0, faceExtras = 0;
        for (int i = 0; i < faceCount && cursor < lines.Count; i++, cursor++)
        {
            var tokens = lines[cursor];
            if (tokens.Length < 1 || !int.TryParse(tokens[0], NumberStyles.Integer, culture, out int k)
                || k < 3 || tokens.Length < 1 + k)
            {
                badFaces++;
                continue;
            }
            if (tokens.Length > 1 + k)
                faceExtras++; // trailing face colors ignored

            var face = new int[k];
            bool ok = true;
            for (int j = 0; j < k; j++)
            {
                if (!int.TryParse(tokens[1 + j], NumberStyles.Integer, culture, out face[j])
                    || face[j] < 0 || face[j] >= rawPositions.Count)
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                rawFaces.Add(face);
            else
                badFaces++;
        }
        if (rawFaces.Count + badFaces < faceCount)
            warnings.Add($"OFF declares {faceCount} faces but only {rawFaces.Count + badFaces} lines were present.");

        if (badVertices > 0)
            warnings.Add($"{badVertices} malformed vertex lines replaced with the origin (indices preserved).");
        if (badFaces > 0)
            warnings.Add($"{badFaces} malformed or out-of-range face lines skipped.");
        if (vertexExtras > 0)
            warnings.Add($"Extra per-vertex values ignored on {vertexExtras} lines (colors/normals).");
        if (faceExtras > 0)
            warnings.Add($"Trailing face color values ignored on {faceExtras} lines.");

        return ObjReader.BuildFromIndexed(rawPositions, rawFaces, weldTolerance, warnings);
    }
}
