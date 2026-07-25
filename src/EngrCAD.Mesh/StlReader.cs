using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// STL import (binary and ASCII, autodetected) — the inverse of <see cref="StlWriter"/>.
/// STL is a bag of disconnected triangles, so coincident vertices are welded back
/// together (spatial hash; representatives keep their exact file coordinates) and the
/// result attempts the manifold <see cref="HalfEdgeMesh.Build"/>. Dirty files return
/// the soup + diagnostics instead (see <see cref="MeshReadResult"/>).
///
/// Format detection is size-based, not header-based: a binary STL is exactly
/// 84 + 50·n bytes (80-byte header + uint32 count + 50-byte facets) and has no magic —
/// while many binary exporters write headers starting with "solid", the ASCII
/// keyword. The exact-size test therefore runs first; the "solid" prefix (plus a
/// printable-text check, since ASCII files can't contain NUL/control bytes) only
/// decides when the size doesn't match.
///
/// Binary STL stores float32 coordinates: coincident vertices are bit-identical, so
/// the default weld tolerance is the 1e-9 weld tier. Note that cracks smaller than
/// one float ulp (~1.2e-7 at coordinate 1.0) cannot exist in a binary file, so
/// repair-time crack welding on STL input needs tolerances at or above float
/// quantization.
/// </summary>
public static class StlReader
{
    /// <param name="path">File to read (.stl, binary or ASCII).</param>
    /// <param name="weldTolerance">Distance under which soup vertices weld to one
    /// representative. Defaults to the 1e-9 absolute weld tier
    /// (<c>Tolerance.Default.Linear</c>) — exact for binary STL, whose shared vertices
    /// are bit-identical floats. Crack closing at looser tolerances is
    /// <see cref="MeshRepair"/>'s job, not the reader's.</param>
    public static MeshReadResult ReadFile(string path, double weldTolerance = 1e-9)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, weldTolerance);
    }

    /// <summary>Reads an STL from a stream (buffered fully — detection needs the total
    /// size). See <see cref="ReadFile"/> for parameter semantics.</summary>
    public static MeshReadResult Read(Stream stream, double weldTolerance = 1e-9)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var warnings = new List<string>();
        return DetectBinary(bytes, warnings)
            ? ReadBinary(bytes, weldTolerance, warnings)
            : ReadAscii(System.Text.Encoding.ASCII.GetString(bytes), weldTolerance, warnings);
    }

    private static bool DetectBinary(byte[] bytes, List<string> warnings)
    {
        // 1. Definitive: exact binary size 84 + 50·n. Runs before any header sniffing
        //    because binary headers routinely begin with "solid".
        if (bytes.Length >= 84)
        {
            uint declared = BitConverter.ToUInt32(bytes, 80);
            if (84L + 50L * declared == bytes.Length)
                return true;
        }

        // 2. ASCII candidate: leading "solid" and a printable prefix. Binary files with
        //    trailing padding can also start with "solid", hence the text check.
        if (StartsWithSolid(bytes))
        {
            if (LooksLikeText(bytes))
                return false;
            warnings.Add("File starts with 'solid' but contains non-text bytes; parsing as binary STL.");
            return true;
        }

        if (bytes.Length >= 84)
        {
            warnings.Add("File size does not match 84 + 50 * n and there is no 'solid' prefix; " +
                         "parsing as binary STL (file may be truncated or padded).");
            return true;
        }

        warnings.Add("File is too short for binary STL; attempting ASCII parse.");
        return false;
    }

    private static bool StartsWithSolid(byte[] bytes)
    {
        int i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            i = 3; // UTF-8 BOM
        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
            i++;
        ReadOnlySpan<byte> keyword = "solid"u8;
        if (i + keyword.Length > bytes.Length)
            return false;
        for (int k = 0; k < keyword.Length; k++)
        {
            if ((bytes[i + k] | 0x20) != keyword[k]) // case-insensitive ASCII
                return false;
        }
        return true;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 500);
        for (int i = 0; i < n; i++)
        {
            byte b = bytes[i];
            if (b is not (9 or 10 or 13) && b is < 32 or > 126)
                return false;
        }
        return true;
    }

    private static MeshReadResult ReadBinary(byte[] bytes, double weldTolerance, List<string> warnings)
    {
        long declared = bytes.Length >= 84 ? BitConverter.ToUInt32(bytes, 80) : 0;
        long available = bytes.Length >= 84 ? (bytes.Length - 84) / 50 : 0;
        long count = Math.Min(declared, available);
        if (count < declared)
            warnings.Add($"Binary STL declares {declared} facets but only {available} complete records are present.");
        else if (available > declared)
            warnings.Add($"Binary STL declares {declared} facets; {(available - declared) * 50 + (bytes.Length - 84) % 50} trailing bytes ignored.");

        var welder = new MeshSoupOps.VertexWelder(weldTolerance);
        var faces = new List<int[]>((int)count);
        int degenerate = 0;
        Span<int> tri = stackalloc int[3];
        for (long i = 0; i < count; i++)
        {
            int offset = (int)(84 + 50 * i) + 12; // skip the facet normal — we derive orientation from winding
            for (int corner = 0; corner < 3; corner++)
            {
                var p = new Vector3d(
                    BitConverter.ToSingle(bytes, offset),
                    BitConverter.ToSingle(bytes, offset + 4),
                    BitConverter.ToSingle(bytes, offset + 8));
                tri[corner] = welder.Add(p);
                offset += 12;
            }
            // attribute byte count (2 bytes) ignored
            if (tri[0] == tri[1] || tri[1] == tri[2] || tri[2] == tri[0])
                degenerate++;
            else
                faces.Add([tri[0], tri[1], tri[2]]);
        }
        return MeshReadResult.Create(welder.Points, faces, warnings, degenerate);
    }

    private static MeshReadResult ReadAscii(string text, double weldTolerance, List<string> warnings)
    {
        var welder = new MeshSoupOps.VertexWelder(weldTolerance);
        var faces = new List<int[]>();
        int degenerate = 0;
        int shortLoops = 0, longLoops = 0, badNumbers = 0;
        var unknown = new Dictionary<string, int>();
        var loop = new List<int>(4);

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;
            switch (tokens[0].ToLowerInvariant())
            {
                case "solid":
                case "endsolid":
                case "facet":     // "facet normal x y z" — normal ignored, winding is authoritative
                case "outer":     // "outer loop"
                    break;
                case "vertex":
                    if (tokens.Length >= 4
                        && double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                        && double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                        && double.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    {
                        loop.Add(welder.Add(new Vector3d(x, y, z)));
                    }
                    else
                    {
                        badNumbers++;
                    }
                    break;
                case "endloop":
                    break;
                case "endfacet":
                    if (loop.Count < 3)
                    {
                        if (loop.Count > 0)
                            shortLoops++;
                    }
                    else
                    {
                        if (loop.Count > 3)
                            longLoops++; // non-standard; fan-triangulate
                        var cleaned = MeshSoupOps.CleanLoop(loop);
                        if (cleaned is null)
                        {
                            degenerate++;
                        }
                        else
                        {
                            for (int i = 1; i + 1 < cleaned.Length; i++)
                                faces.Add([cleaned[0], cleaned[i], cleaned[i + 1]]);
                        }
                    }
                    loop.Clear();
                    break;
                default:
                    unknown.TryGetValue(tokens[0], out int seen);
                    unknown[tokens[0]] = seen + 1;
                    break;
            }
        }

        if (loop.Count > 0)
            warnings.Add("ASCII STL ended inside a facet; the incomplete facet was dropped.");
        if (badNumbers > 0)
            warnings.Add($"{badNumbers} vertex lines could not be parsed and were skipped.");
        if (shortLoops > 0)
            warnings.Add($"{shortLoops} facets had fewer than 3 vertices and were skipped.");
        if (longLoops > 0)
            warnings.Add($"{longLoops} facets had more than 3 vertices (non-standard) and were fan-triangulated.");
        foreach (var (keyword, n) in unknown)
            warnings.Add($"Unknown ASCII STL keyword '{keyword}' skipped ({n} lines).");

        return MeshReadResult.Create(welder.Points, faces, warnings, degenerate);
    }
}
