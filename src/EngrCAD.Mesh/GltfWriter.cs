using System.Text;
using System.Text.Json;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// glTF 2.0 export (binary <c>.glb</c> and self-contained JSON <c>.gltf</c>) — the web,
/// AR and DCC interchange format, dependency-free over <see cref="Utf8JsonWriter"/> and a
/// hand-written GLB container.
/// <para><b>The seam is a node FOREST over shared geometry, not a flat part list.</b>
/// glTF has real hierarchy, so unlike STL/OBJ/3MF (which bake every transform into the
/// vertices) this exporter preserves it: one <see cref="GltfGeometry"/> per distinct piece
/// of geometry becomes one glTF mesh, and every place it is used becomes a
/// <see cref="GltfNode"/> carrying its own matrix — the same "one product, N occurrences"
/// structure the STEP assembly writer emits. A part placed fifty times is written once.
/// The flat <see cref="MeshExportPart"/> overload is a convenience that builds a
/// one-node-per-part forest.</para>
/// <para><b>Winding is NOT flipped under mirroring</b>, which is the one place this
/// differs from the baking writers. The glTF spec requires a consumer to reverse the
/// winding itself when a node's global transform has a negative determinant, so writing
/// the transform verbatim is both correct and lossless — flipping here would double the
/// correction and turn every mirrored instance inside out.</para>
/// <para><b>Coordinates are converted at ONE root node.</b> glTF is Y-up and metres;
/// EngrCAD is Z-up and millimetres (the convention 3MF's <c>unit="millimeter"</c> and the
/// STEP reader already assume). <see cref="GltfOptions"/> puts that conversion on a single
/// root node built from EXACT values (no <c>cos(-pi/2)</c> = 6.1e-17 noise), so every
/// part transform below it stays verbatim and readable, and turning the conversion off
/// leaves the file in model coordinates.</para>
/// <para>Geometry is the FLAT render mesh (<see cref="RenderMesh.CreateFlat"/>): per-face
/// normals, so a CAD model keeps its hard edges. That costs three vertices per triangle
/// against a shared-vertex mesh, which is the honest trade — smooth normals would round
/// off every machined edge.</para>
/// <para>Colours are written verbatim as linear <c>pbrMetallicRoughness.baseColorFactor</c>
/// values rather than converted from sRGB, so a part looks in a glTF viewer the way it
/// looks in EngrCAD's own viewport, which applies no gamma conversion either.</para>
/// </summary>
public static class GltfWriter
{
    private const uint Magic = 0x46546C67;      // "glTF"
    private const uint JsonChunkType = 0x4E4F534A; // "JSON"
    private const uint BinChunkType = 0x004E4942;  // "BIN\0"

    private const int ComponentFloat = 5126;    // GL_FLOAT
    private const int ComponentUnsignedInt = 5125; // GL_UNSIGNED_INT
    private const int TargetArrayBuffer = 34962;
    private const int TargetElementArrayBuffer = 34963;
    private const int ModeTriangles = 4;

    /// <summary>The colour a geometry with no colour of its own is given. glTF's own
    /// default material is metallic 1 / roughness 1, which renders as black chrome — a
    /// neutral dielectric grey is what an untextured CAD part should look like.</summary>
    private static readonly (float R, float G, float B) DefaultColor = (0.78f, 0.78f, 0.80f);

    // ---- entry points ----

    /// <summary>Writes a binary <c>.glb</c>: a 12-byte header, the JSON chunk, then the
    /// binary buffer chunk.</summary>
    public static void WriteGlb(
        IReadOnlyList<GltfGeometry> geometries,
        IReadOnlyList<GltfNode> roots,
        Stream stream,
        GltfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var (json, bin) = Build(geometries, roots, options ?? GltfOptions.Default, indented: false, embedBuffer: false);

        int jsonPadding = Pad4(json.Length);
        int binPadding = Pad4(bin.Length);
        int total = 12 + 8 + json.Length + jsonPadding + (bin.Length == 0 ? 0 : 8 + bin.Length + binPadding);

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(2u); // glTF version
        writer.Write((uint)total);

        writer.Write((uint)(json.Length + jsonPadding));
        writer.Write(JsonChunkType);
        writer.Write(json);
        // The JSON chunk pads with SPACES so the padding stays inside valid JSON text;
        // the BIN chunk pads with zeros. Getting these two the same way round is the
        // classic GLB bug, and a validator reports it as trailing garbage.
        for (int i = 0; i < jsonPadding; i++)
            writer.Write((byte)0x20);

        if (bin.Length != 0)
        {
            writer.Write((uint)(bin.Length + binPadding));
            writer.Write(BinChunkType);
            writer.Write(bin);
            for (int i = 0; i < binPadding; i++)
                writer.Write((byte)0);
        }
    }

    /// <summary>Writes a self-contained JSON <c>.gltf</c>: the buffer rides inline as a
    /// base64 data URI, so there is no sidecar <c>.bin</c> to lose.</summary>
    public static void WriteGltf(
        IReadOnlyList<GltfGeometry> geometries,
        IReadOnlyList<GltfNode> roots,
        Stream stream,
        GltfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var (json, _) = Build(geometries, roots, options ?? GltfOptions.Default, indented: true, embedBuffer: true);
        stream.Write(json, 0, json.Length);
    }

    /// <summary>Writes to a file, picking the container from the extension:
    /// <c>.glb</c> binary, <c>.gltf</c> self-contained JSON.</summary>
    public static void WriteFile(
        IReadOnlyList<GltfGeometry> geometries,
        IReadOnlyList<GltfNode> roots,
        string path,
        GltfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.Create(path);
        if (string.Equals(Path.GetExtension(path), ".gltf", StringComparison.OrdinalIgnoreCase))
            WriteGltf(geometries, roots, stream, options);
        else
            WriteGlb(geometries, roots, stream, options);
    }

    /// <summary>Convenience over the flat <see cref="MeshExportPart"/> seam the other
    /// multi-part exporters take: one geometry and one root node per part, the part's
    /// transform on its node (NOT baked into the vertices, so an instanced scene stays
    /// instanced if the caller shares meshes).</summary>
    public static void WriteFile(
        IReadOnlyList<MeshExportPart> parts, string path, GltfOptions? options = null)
    {
        var (geometries, roots) = FromParts(parts);
        WriteFile(geometries, roots, path, options);
    }

    /// <summary>Binary <c>.glb</c> over the flat <see cref="MeshExportPart"/> seam.</summary>
    public static void WriteGlb(
        IReadOnlyList<MeshExportPart> parts, Stream stream, GltfOptions? options = null)
    {
        var (geometries, roots) = FromParts(parts);
        WriteGlb(geometries, roots, stream, options);
    }

    public static void WriteFile(HalfEdgeMesh mesh, string path, string name = "part") =>
        WriteFile([new MeshExportPart(mesh, name)], path);

    private static (List<GltfGeometry> Geometries, List<GltfNode> Roots) FromParts(
        IReadOnlyList<MeshExportPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var geometries = new List<GltfGeometry>(parts.Count);
        var roots = new List<GltfNode>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            geometries.Add(new GltfGeometry(part.Mesh, part.Name) { Color = part.Color });
            roots.Add(new GltfNode(part.Name) { Transform = part.Transform, Geometry = i });
        }
        return (geometries, roots);
    }

    // ---- document assembly ----

    private static (byte[] Json, byte[] Bin) Build(
        IReadOnlyList<GltfGeometry> geometries,
        IReadOnlyList<GltfNode> roots,
        GltfOptions options,
        bool indented,
        bool embedBuffer)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Scale <= 0 || !double.IsFinite(options.Scale))
            throw new ArgumentException(
                $"glTF export scale must be finite and positive (got {options.Scale}).", nameof(options));

        var bin = new MemoryStream();
        var views = new List<BufferView>();
        var accessors = new List<Accessor>();
        var materials = new List<(float R, float G, float B, float A)>();
        var materialIndex = new Dictionary<(float, float, float, float), int>();
        var meshes = new List<MeshRecord>();
        // A geometry with no triangles produces no glTF mesh (the spec requires
        // primitives to be non-empty); its nodes are then written mesh-less rather than
        // dangling, so an empty part cannot corrupt the file.
        var meshOf = new int[geometries.Count];

        for (int g = 0; g < geometries.Count; g++)
        {
            var geometry = geometries[g];
            ArgumentNullException.ThrowIfNull(geometry.Mesh);
            var render = RenderMesh.CreateFlat(geometry.Mesh);
            if (render.TriangleCount == 0)
            {
                meshOf[g] = -1;
                continue;
            }

            int position = AddAccessor(
                bin, views, accessors, render.Positions, 3, TargetArrayBuffer, withBounds: true);
            int normal = AddAccessor(
                bin, views, accessors, UnitNormals(render), 3, TargetArrayBuffer, withBounds: false);
            int? color = null;
            if (geometry.VertexColors is { } vertexColors)
            {
                color = AddAccessor(
                    bin, views, accessors, SpreadColors(geometry, render, vertexColors), 4,
                    TargetArrayBuffer, withBounds: false);
            }
            int indices = AddIndexAccessor(bin, views, accessors, render.Indices);

            // A primitive carrying COLOR_0 gets a WHITE base factor: glTF multiplies the
            // two, so leaving the part colour in would tint every field value by it and
            // a viridis ramp would come out the colour of the part.
            var (r, gb, bb) = geometry.VertexColors is not null
                ? (1f, 1f, 1f)
                : geometry.Color ?? DefaultColor;
            var baseColor = (r, gb, bb, geometry.Opacity);
            if (!materialIndex.TryGetValue(baseColor, out int material))
            {
                material = materials.Count;
                materialIndex[baseColor] = material;
                materials.Add(baseColor);
            }

            meshOf[g] = meshes.Count;
            meshes.Add(new MeshRecord(geometry.Name, position, normal, color, indices, material));
        }

        // Node flattening: glTF nodes are a flat array referenced by index, and the tree
        // is the children lists. The forest is walked depth first so a file reads in the
        // order the document does.
        var nodes = new List<NodeRecord>();
        var visited = new HashSet<GltfNode>(ReferenceEqualityComparer.Instance);
        var rootIndices = new List<int>();
        foreach (var root in roots)
            rootIndices.Add(Flatten(root, nodes, meshOf, geometries.Count, visited));

        // The unit/orientation conversion is ONE node wrapping the caller's forest. Built
        // exactly: (x, y, z) -> (s*x, s*z, -s*y) takes Z-up millimetres to Y-up metres
        // with no transcendental anywhere near it.
        if (options.YUp || options.Scale != 1)
        {
            double s = options.Scale;
            var convert = options.YUp
                ? new Matrix4d(s, 0, 0, 0, 0, 0, s, 0, 0, -s, 0, 0, 0, 0, 0, 1)
                : new Matrix4d(s, 0, 0, 0, 0, s, 0, 0, 0, 0, s, 0, 0, 0, 0, 1);
            nodes.Add(new NodeRecord(options.RootName, convert, null, [.. rootIndices]));
            rootIndices = [nodes.Count - 1];
        }

        var json = Serialize(
            nodes, rootIndices, meshes, materials, accessors, views,
            checked((int)bin.Length), options, indented,
            embedBuffer ? bin.ToArray() : null);
        return (json, bin.ToArray());
    }

    private static int Flatten(
        GltfNode node, List<NodeRecord> nodes, int[] meshOf, int geometryCount,
        HashSet<GltfNode> visited)
    {
        ArgumentNullException.ThrowIfNull(node);
        // glTF nodes form a forest: one parent each, no cycles. Reference identity is the
        // right test — the same node object appearing twice would be written once and
        // referenced twice, which is exactly the malformed graph the spec forbids.
        if (!visited.Add(node))
            throw new ArgumentException(
                $"glTF node '{node.Name}' appears more than once in the hierarchy; "
                + "glTF nodes form a forest, so a node used in two places must be two node "
                + "objects (sharing one geometry index is how instancing is expressed).");

        if (node.Geometry is { } g && (g < 0 || g >= geometryCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(node),
                $"glTF node '{node.Name}' references geometry {g}, but only {geometryCount} "
                + "were supplied.");
        }

        var children = new int[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
            children[i] = Flatten(node.Children[i], nodes, meshOf, geometryCount, visited);

        int mesh = node.Geometry is { } index ? meshOf[index] : -1;
        nodes.Add(new NodeRecord(node.Name, node.Transform, mesh < 0 ? null : mesh, children));
        return nodes.Count - 1;
    }

    /// <summary>Renormalizes the render mesh's normals. glTF requires unit normals, and
    /// <see cref="Face.Normal"/> returns the exact zero vector for a degenerate facet —
    /// which a validator rejects — so a zero is replaced by an arbitrary unit vector
    /// rather than written out.</summary>
    private static float[] UnitNormals(RenderMesh render)
    {
        var normals = (float[])render.Normals.Clone();
        for (int i = 0; i < normals.Length; i += 3)
        {
            double x = normals[i], y = normals[i + 1], z = normals[i + 2];
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length == 0)
            {
                normals[i + 2] = 1f;
                continue;
            }
            normals[i] = (float)(x / length);
            normals[i + 1] = (float)(y / length);
            normals[i + 2] = (float)(z / length);
        }
        return normals;
    }

    /// <summary>Spreads per-SOURCE-vertex colours across the flat render mesh's
    /// duplicates via <see cref="RenderMesh.SourceVertices"/> — the exact placement rule
    /// the field renderer uses, never a position hash (two distinct source vertices may
    /// share a position).</summary>
    private static float[] SpreadColors(
        GltfGeometry geometry, RenderMesh render,
        IReadOnlyList<(float R, float G, float B)> vertexColors)
    {
        int sourceCount = geometry.Mesh.VertexCount;
        if (vertexColors.Count != sourceCount)
        {
            throw new ArgumentException(
                $"glTF geometry '{geometry.Name}': {vertexColors.Count} vertex colours were "
                + $"supplied for a mesh with {sourceCount} vertices. Colours are indexed by "
                + "the source mesh's vertices, in vertex order.");
        }
        if (render.SourceVertices.Length != render.VertexCount)
        {
            throw new ArgumentException(
                $"glTF geometry '{geometry.Name}': the render mesh carries no source-vertex "
                + "map, so per-vertex colours cannot be placed on it.");
        }

        var colors = new float[render.VertexCount * 4];
        for (int v = 0; v < render.VertexCount; v++)
        {
            var (r, g, b) = vertexColors[render.SourceVertices[v]];
            colors[v * 4] = r;
            colors[v * 4 + 1] = g;
            colors[v * 4 + 2] = b;
            colors[v * 4 + 3] = geometry.Opacity;
        }
        return colors;
    }

    // ---- buffer plumbing ----

    private static int AddAccessor(
        MemoryStream bin, List<BufferView> views, List<Accessor> accessors,
        float[] values, int components, int target, bool withBounds)
    {
        int offset = checked((int)bin.Length);
        // Every component here is 4 bytes and every view starts at a multiple of 4, so
        // the spec's "accessor offset must be a multiple of the component size" holds by
        // construction. Asserted rather than assumed: a future 2-byte index type would
        // break it silently.
        if (offset % 4 != 0)
            throw new InvalidOperationException($"glTF buffer view offset {offset} is not 4-aligned.");

        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        bin.Write(bytes, 0, bytes.Length);

        views.Add(new BufferView(offset, bytes.Length, target));
        int count = values.Length / components;

        float[]? min = null, max = null;
        if (withBounds)
        {
            min = new float[components];
            max = new float[components];
            for (int c = 0; c < components; c++)
            {
                min[c] = float.PositiveInfinity;
                max[c] = float.NegativeInfinity;
            }
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    float value = values[i * components + c];
                    if (value < min[c]) min[c] = value;
                    if (value > max[c]) max[c] = value;
                }
            }
        }

        accessors.Add(new Accessor(
            views.Count - 1, ComponentFloat, count, TypeName(components), min, max));
        return accessors.Count - 1;
    }

    private static int AddIndexAccessor(
        MemoryStream bin, List<BufferView> views, List<Accessor> accessors, uint[] indices)
    {
        int offset = checked((int)bin.Length);
        var bytes = new byte[indices.Length * sizeof(uint)];
        Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
        bin.Write(bytes, 0, bytes.Length);
        views.Add(new BufferView(offset, bytes.Length, TargetElementArrayBuffer));
        accessors.Add(new Accessor(
            views.Count - 1, ComponentUnsignedInt, indices.Length, "SCALAR", null, null));
        return accessors.Count - 1;
    }

    private static string TypeName(int components) => components switch
    {
        1 => "SCALAR",
        2 => "VEC2",
        3 => "VEC3",
        4 => "VEC4",
        _ => throw new ArgumentOutOfRangeException(nameof(components)),
    };

    private static int Pad4(int length) => (4 - (length % 4)) % 4;

    // ---- JSON ----

    private static byte[] Serialize(
        List<NodeRecord> nodes, List<int> rootIndices, List<MeshRecord> meshes,
        List<(float R, float G, float B, float A)> materials, List<Accessor> accessors,
        List<BufferView> views, int bufferLength, GltfOptions options, bool indented,
        byte[]? embedded)
    {
        var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            w.WriteStartObject();

            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", options.Generator);
            w.WriteEndObject();

            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            if (options.SceneName is { } sceneName)
                w.WriteString("name", sceneName);
            w.WriteStartArray("nodes");
            foreach (int index in rootIndices)
                w.WriteNumberValue(index);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            foreach (var node in nodes)
            {
                w.WriteStartObject();
                w.WriteString("name", node.Name);
                if (!node.Transform.Equals(Matrix4d.Identity))
                {
                    // glTF stores a matrix COLUMN-major; Matrix4d is row-major storage
                    // with a column-vector convention, so entry 4*col + row is Mrow,col.
                    w.WriteStartArray("matrix");
                    foreach (double value in ColumnMajor(node.Transform))
                        w.WriteNumberValue(value);
                    w.WriteEndArray();
                }
                if (node.Mesh is { } mesh)
                    w.WriteNumber("mesh", mesh);
                if (node.Children.Length != 0)
                {
                    w.WriteStartArray("children");
                    foreach (int child in node.Children)
                        w.WriteNumberValue(child);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (meshes.Count != 0)
            {
                w.WriteStartArray("meshes");
                foreach (var mesh in meshes)
                {
                    w.WriteStartObject();
                    w.WriteString("name", mesh.Name);
                    w.WriteStartArray("primitives");
                    w.WriteStartObject();
                    w.WriteStartObject("attributes");
                    w.WriteNumber("POSITION", mesh.Position);
                    w.WriteNumber("NORMAL", mesh.Normal);
                    if (mesh.Color is { } color)
                        w.WriteNumber("COLOR_0", color);
                    w.WriteEndObject();
                    w.WriteNumber("indices", mesh.Indices);
                    w.WriteNumber("material", mesh.Material);
                    w.WriteNumber("mode", ModeTriangles);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (materials.Count != 0)
            {
                w.WriteStartArray("materials");
                for (int i = 0; i < materials.Count; i++)
                {
                    var (r, g, b, a) = materials[i];
                    w.WriteStartObject();
                    w.WriteString("name", $"material{i}");
                    w.WriteStartObject("pbrMetallicRoughness");
                    w.WriteStartArray("baseColorFactor");
                    w.WriteNumberValue(r);
                    w.WriteNumberValue(g);
                    w.WriteNumberValue(b);
                    w.WriteNumberValue(a);
                    w.WriteEndArray();
                    w.WriteNumber("metallicFactor", options.Metallic);
                    w.WriteNumber("roughnessFactor", options.Roughness);
                    w.WriteEndObject();
                    // Fills are drawn from both sides in this kernel's viewer (a section
                    // plane exposes interiors as backfaces), so the exported material is
                    // double sided for the same reason.
                    w.WriteBoolean("doubleSided", true);
                    if (a < 1f)
                        w.WriteString("alphaMode", "BLEND");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (accessors.Count != 0)
            {
                w.WriteStartArray("accessors");
                foreach (var accessor in accessors)
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", accessor.BufferView);
                    w.WriteNumber("componentType", accessor.ComponentType);
                    w.WriteNumber("count", accessor.Count);
                    w.WriteString("type", accessor.Type);
                    if (accessor.Min is { } min)
                    {
                        w.WriteStartArray("min");
                        foreach (float value in min)
                            w.WriteNumberValue(value);
                        w.WriteEndArray();
                    }
                    if (accessor.Max is { } max)
                    {
                        w.WriteStartArray("max");
                        foreach (float value in max)
                            w.WriteNumberValue(value);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (views.Count != 0)
            {
                w.WriteStartArray("bufferViews");
                foreach (var view in views)
                {
                    w.WriteStartObject();
                    w.WriteNumber("buffer", 0);
                    w.WriteNumber("byteOffset", view.ByteOffset);
                    w.WriteNumber("byteLength", view.ByteLength);
                    w.WriteNumber("target", view.Target);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (bufferLength != 0)
            {
                w.WriteStartArray("buffers");
                w.WriteStartObject();
                w.WriteNumber("byteLength", bufferLength);
                if (embedded is not null)
                {
                    w.WriteString(
                        "uri", "data:application/octet-stream;base64," + Convert.ToBase64String(embedded));
                }
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static double[] ColumnMajor(in Matrix4d m) =>
    [
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44,
    ];

    private readonly record struct BufferView(int ByteOffset, int ByteLength, int Target);

    private readonly record struct Accessor(
        int BufferView, int ComponentType, int Count, string Type, float[]? Min, float[]? Max);

    private readonly record struct MeshRecord(
        string Name, int Position, int Normal, int? Color, int Indices, int Material);

    private readonly record struct NodeRecord(
        string Name, Matrix4d Transform, int? Mesh, int[] Children);
}

/// <summary>
/// One distinct piece of geometry in a glTF export: it becomes one glTF mesh, referenced
/// by every <see cref="GltfNode"/> that places it. Sharing a geometry between nodes is
/// how instancing is expressed — a fastener placed fifty times is written once.
/// </summary>
/// <param name="Mesh">The geometry, in model coordinates (node transforms place it).</param>
/// <param name="Name">The exported mesh name.</param>
public sealed record GltfGeometry(HalfEdgeMesh Mesh, string Name)
{
    /// <summary>Display colour as linear RGB in [0, 1]; null takes a neutral grey.</summary>
    public (float R, float G, float B)? Color { get; init; }

    /// <summary>Opacity in [0, 1]; below 1 the material declares <c>alphaMode: BLEND</c>.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>
    /// Optional per-vertex colours indexed by the SOURCE mesh's vertices (the same
    /// indexing a simulation result uses), spread across the flat render mesh's
    /// duplicates on write. Present colours become the <c>COLOR_0</c> attribute and force
    /// a white base colour factor, since glTF multiplies the two.
    /// </summary>
    public IReadOnlyList<(float R, float G, float B)>? VertexColors { get; init; }
}

/// <summary>
/// A node in the exported hierarchy: a name, a local transform, an optional geometry, and
/// children. glTF nodes form a FOREST — each node object may appear once, so two
/// placements of one part are two nodes sharing a <see cref="Geometry"/> index.
/// </summary>
public sealed record GltfNode(string Name)
{
    /// <summary>Transform relative to the parent node (identity is omitted from the file).</summary>
    public Matrix4d Transform { get; init; } = Matrix4d.Identity;

    /// <summary>Index into the geometry list, or null for a pure grouping node.</summary>
    public int? Geometry { get; init; }

    public IReadOnlyList<GltfNode> Children { get; init; } = [];
}

/// <summary>Export settings for <see cref="GltfWriter"/>.</summary>
public sealed record GltfOptions
{
    public static readonly GltfOptions Default = new();

    /// <summary>Convert Z-up model coordinates to glTF's Y-up convention on the root
    /// node. On by default — every glTF consumer assumes Y-up, so a Z-up file arrives
    /// lying on its side.</summary>
    public bool YUp { get; init; } = true;

    /// <summary>Root-node scale. Defaults to 0.001: model units are millimetres (the
    /// convention 3MF and the STEP reader already assume) and glTF units are metres.</summary>
    public double Scale { get; init; } = 0.001;

    /// <summary>Name of the conversion root node.</summary>
    public string RootName { get; init; } = "EngrCAD";

    public string? SceneName { get; init; }

    public string Generator { get; init; } = "EngrCAD";

    /// <summary>PBR metallic factor for every exported material. Low but not zero: CAD
    /// parts read as machined metal rather than plastic.</summary>
    public float Metallic { get; init; } = 0.1f;

    public float Roughness { get; init; } = 0.55f;
}
