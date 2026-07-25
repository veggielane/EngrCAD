namespace EngrCAD.Mesh;

/// <summary>
/// Format-dispatching mesh import facade: routes by file extension to
/// <see cref="StlReader"/> (.stl, binary/ASCII autodetected), <see cref="ObjReader"/>
/// (.obj), or <see cref="OffReader"/> (.off). See <see cref="MeshReadResult"/> for the
/// contract: dirty files return the welded soup + diagnostics instead of throwing.
/// </summary>
public static class MeshReader
{
    /// <summary>True when <see cref="ReadFile"/> can handle the extension
    /// (with or without the leading dot, any case).</summary>
    public static bool SupportsExtension(string extension)
    {
        var e = Normalize(extension);
        return e is ".stl" or ".obj" or ".off";
    }

    /// <param name="path">Mesh file to read (.stl, .obj, or .off).</param>
    /// <param name="weldTolerance">Distance under which coincident vertices weld to
    /// one representative; defaults to the 1e-9 absolute weld tier
    /// (<c>Tolerance.Default.Linear</c>). See the per-format readers for caveats
    /// (binary STL is float32-quantized).</param>
    public static MeshReadResult ReadFile(string path, double weldTolerance = 1e-9) =>
        Normalize(Path.GetExtension(path)) switch
        {
            ".stl" => StlReader.ReadFile(path, weldTolerance),
            ".obj" => ObjReader.ReadFile(path, weldTolerance),
            ".off" => OffReader.ReadFile(path, weldTolerance),
            var e => throw new NotSupportedException(
                $"Unsupported mesh format '{e}'. Supported extensions: .stl, .obj, .off."),
        };

    private static string Normalize(string extension) =>
        (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();
}
