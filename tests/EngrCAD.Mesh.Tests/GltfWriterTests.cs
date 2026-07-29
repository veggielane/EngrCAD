using System.Text;
using System.Text.Json;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// glTF 2.0 export. Every test runs the written file through <see cref="Gltf"/>, a
/// hand-written structural validator that checks the rules a glTF consumer relies on —
/// chunk headers and 4-byte padding, accessor bounds and alignment, POSITION min/max
/// actually bounding the data, indices in range, and the node graph being a forest — so
/// a malformed file fails here rather than in someone's browser.
/// </summary>
public class GltfWriterTests
{
    // ---- container structure ----

    [Fact]
    public void Glb_HasAValidContainerAndJsonChunk()
    {
        var bytes = WriteGlb([new MeshExportPart(MeshPrimitives.Box(10, 20, 30), "block")]);
        var gltf = Gltf.Parse(bytes);

        Assert.Equal("2.0", gltf.Json.GetProperty("asset").GetProperty("version").GetString());
        Assert.Equal("EngrCAD", gltf.Json.GetProperty("asset").GetProperty("generator").GetString());
        gltf.Validate();
    }

    [Fact]
    public void Glb_ChunksAreFourBytePaddedWithTheSpecifiedFillBytes()
    {
        // A name of odd length pushes the JSON past a 4-byte boundary, which is exactly
        // when the padding rule bites: JSON pads with SPACES (so the chunk stays valid
        // JSON text) and BIN with zeros. Swapping them is the classic GLB bug.
        var bytes = WriteGlb([new MeshExportPart(MeshPrimitives.Box(1, 1, 1), "abcde")]);
        var gltf = Gltf.Parse(bytes);

        Assert.Equal(0, gltf.JsonChunkLength % 4);
        Assert.Equal(0, gltf.BinChunkLength % 4);
        foreach (byte b in gltf.JsonPadding)
            Assert.Equal(0x20, b);
        foreach (byte b in gltf.BinPadding)
            Assert.Equal(0, b);
        Assert.Equal((uint)bytes.Length, gltf.DeclaredLength);
    }

    [Fact]
    public void Glb_RejectsNothing_ButAnEmptyMeshProducesNoPrimitive()
    {
        // A geometry with no triangles must not produce an empty primitives array (the
        // spec forbids it); its node is written mesh-less instead.
        var empty = HalfEdgeMesh.Build([], Array.Empty<IReadOnlyList<int>>());
        var geometries = new List<GltfGeometry> { new(empty, "nothing") };
        var roots = new List<GltfNode> { new("nothing") { Geometry = 0 } };

        using var stream = new MemoryStream();
        GltfWriter.WriteGlb(geometries, roots, stream);
        var gltf = Gltf.Parse(stream.ToArray());
        gltf.Validate();

        Assert.False(gltf.Json.TryGetProperty("meshes", out _));
        var node = gltf.Nodes.Single(n => n.GetProperty("name").GetString() == "nothing");
        Assert.False(node.TryGetProperty("mesh", out _));
    }

    // ---- geometry fidelity ----

    [Fact]
    public void ExportedTrianglesReproduceTheModelVolumeInMetres()
    {
        // The strongest end-to-end check available: decode the buffer, walk the triangles
        // through the node transforms, and sum the signed tetrahedral volume. It catches
        // a wrong matrix convention, a wrong winding, a wrong accessor stride and the
        // unit conversion all at once.
        var mesh = MeshPrimitives.Box(10, 20, 30);
        var bytes = WriteGlb([new MeshExportPart(mesh, "block")]);
        var gltf = Gltf.Parse(bytes);
        gltf.Validate();

        // 10 x 20 x 30 mm = 6000 mm^3 = 6e-6 m^3.
        Assert.Equal(6000.0 * 1e-9, gltf.SignedVolume(), 15);
    }

    [Fact]
    public void ModelCoordinatesRoundTripWhenTheConversionIsOff()
    {
        var mesh = MeshPrimitives.Box(10, 20, 30);
        var options = new GltfOptions { YUp = false, Scale = 1 };
        var bytes = WriteGlb([new MeshExportPart(mesh, "block")], options);
        var gltf = Gltf.Parse(bytes);
        gltf.Validate();

        Assert.Equal(mesh.SignedVolume(), gltf.SignedVolume(), 9);
        // Nothing to convert means no wrapper node: the caller's forest IS the scene.
        Assert.Single(gltf.Nodes);
    }

    [Fact]
    public void TheConversionRootIsBuiltExactly()
    {
        // cos(-pi/2) is 6.1e-17, not 0. The conversion matrix is constructed from exact
        // values so a diffable file has no transcendental noise in it.
        var bytes = WriteGlb([new MeshExportPart(MeshPrimitives.Box(1, 1, 1), "b")]);
        var gltf = Gltf.Parse(bytes);
        var root = gltf.Nodes[gltf.RootNodeIndices.Single()];
        var m = root.GetProperty("matrix").EnumerateArray().Select(v => v.GetDouble()).ToArray();

        // Column-major (s, 0, 0, 0 | 0, 0, -s, 0 | 0, s, 0, 0 | 0, 0, 0, 1).
        Assert.Equal(new[] { 0.001, 0, 0, 0, 0, 0, -0.001, 0, 0, 0.001, 0, 0, 0, 0, 0, 1 }, m);
    }

    [Fact]
    public void PositionAccessorsCarryMinAndMaxThatBoundTheData()
    {
        var bytes = WriteGlb([new MeshExportPart(MeshPrimitives.UvSphere(7, 16, 10), "ball")]);
        var gltf = Gltf.Parse(bytes);
        gltf.Validate(); // asserts the bound relation

        int accessor = gltf.Meshes[0]
            .GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var min = gltf.Accessors[accessor].GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var max = gltf.Accessors[accessor].GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(-7.0, min[c], 4);
            Assert.Equal(7.0, max[c], 4);
        }
    }

    [Fact]
    public void NormalsAreUnitLength()
    {
        var bytes = WriteGlb([new MeshExportPart(MeshPrimitives.Cylinder(4, 9, 24), "pin")]);
        var gltf = Gltf.Parse(bytes);
        int accessor = gltf.Meshes[0]
            .GetProperty("primitives")[0].GetProperty("attributes").GetProperty("NORMAL").GetInt32();

        foreach (var n in gltf.ReadVec3(accessor))
            Assert.Equal(1.0, n.Length, 5);
    }

    // ---- hierarchy and instancing ----

    [Fact]
    public void ANodeForestIsPreservedAndSharedGeometryIsWrittenOnce()
    {
        var mesh = MeshPrimitives.Box(2, 2, 2);
        var geometries = new List<GltfGeometry> { new(mesh, "bolt") };
        var roots = new List<GltfNode>
        {
            new("carrier")
            {
                Children =
                [
                    new GltfNode("bolt.1") { Geometry = 0, Transform = Matrix4d.CreateTranslation(new Vector3d(10, 0, 0)) },
                    new GltfNode("bolt.2") { Geometry = 0, Transform = Matrix4d.CreateTranslation(new Vector3d(-10, 0, 0)) },
                ],
            },
        };

        using var stream = new MemoryStream();
        GltfWriter.WriteGlb(geometries, roots, stream);
        var gltf = Gltf.Parse(stream.ToArray());
        gltf.Validate();

        // ONE mesh, two nodes referencing it: that is what instancing means in glTF, and
        // it is the property the baking writers (STL/3MF) structurally cannot have.
        Assert.Single(gltf.Meshes);
        Assert.Equal(2, gltf.Nodes.Count(n => n.TryGetProperty("mesh", out _)));

        var carrier = gltf.Nodes.Single(n => n.GetProperty("name").GetString() == "carrier");
        Assert.Equal(2, carrier.GetProperty("children").GetArrayLength());

        // Two 2x2x2 boxes 20 mm apart: volume is additive whatever the hierarchy does.
        Assert.Equal(2 * 8.0 * 1e-9, gltf.SignedVolume(), 15);
    }

    [Fact]
    public void ANodeUsedTwiceIsRefusedByName()
    {
        var shared = new GltfNode("shared") { Geometry = 0 };
        var geometries = new List<GltfGeometry> { new(MeshPrimitives.Box(1, 1, 1), "g") };
        var roots = new List<GltfNode>
        {
            new("a") { Children = [shared] },
            new("b") { Children = [shared] },
        };

        using var stream = new MemoryStream();
        var error = Assert.Throws<ArgumentException>(
            () => GltfWriter.WriteGlb(geometries, roots, stream));
        Assert.Contains("shared", error.Message);
        Assert.Contains("forest", error.Message);
    }

    [Fact]
    public void AGeometryIndexOutOfRangeIsRefusedByName()
    {
        var geometries = new List<GltfGeometry> { new(MeshPrimitives.Box(1, 1, 1), "g") };
        var roots = new List<GltfNode> { new("stray") { Geometry = 4 } };

        using var stream = new MemoryStream();
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => GltfWriter.WriteGlb(geometries, roots, stream));
        Assert.Contains("stray", error.Message);
    }

    [Fact]
    public void AMirroredTransformIsWrittenVerbatimAndTheWindingIsNotFlipped()
    {
        // glTF requires the CONSUMER to reverse winding when a node's global transform has
        // a negative determinant. Flipping here as well would double the correction.
        var mesh = MeshPrimitives.Box(4, 4, 4);
        var mirror = Matrix4d.CreateScale(new Vector3d(-1, 1, 1));
        var bytes = WriteGlb(
            [new MeshExportPart(mesh, mirror, "mirrored")],
            new GltfOptions { YUp = false, Scale = 1 });
        var gltf = Gltf.Parse(bytes);
        gltf.Validate();

        // The node matrix carries the mirror...
        var node = gltf.Nodes.Single(n => n.GetProperty("name").GetString() == "mirrored");
        Assert.Equal(-1.0, node.GetProperty("matrix")[0].GetDouble());

        // ...and the raw triangle winding is untouched, so the un-corrected volume comes
        // out NEGATIVE. A consumer honouring the spec flips it back to +64.
        Assert.Equal(-64.0, gltf.SignedVolume(), 9);
    }

    // ---- materials and vertex colours ----

    [Fact]
    public void PartColorsBecomeDedupedPbrMaterials()
    {
        var red = (1f, 0f, 0f);
        var bytes = WriteGlb(
        [
            new MeshExportPart(MeshPrimitives.Box(1, 1, 1), Matrix4d.Identity, "a", red),
            new MeshExportPart(MeshPrimitives.Box(1, 1, 1), Matrix4d.Identity, "b", red),
            new MeshExportPart(MeshPrimitives.Box(1, 1, 1), Matrix4d.Identity, "c", (0f, 0f, 1f)),
        ]);
        var gltf = Gltf.Parse(bytes);
        gltf.Validate();

        Assert.Equal(2, gltf.Materials.Length);
        var first = gltf.Materials[0].GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorFactor").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal(new[] { 1f, 0f, 0f, 1f }, first);
        // Two parts share material 0, the third takes material 1.
        var used = gltf.Meshes.Select(m => m.GetProperty("primitives")[0].GetProperty("material").GetInt32());
        Assert.Equal([0, 0, 1], used);
    }

    [Fact]
    public void APartWithNoColorTakesANeutralGreyRatherThanGltfsBlackChromeDefault()
    {
        var bytes = WriteGlb([new MeshExportPart(MeshPrimitives.Box(1, 1, 1), "plain")]);
        var gltf = Gltf.Parse(bytes);
        var factor = gltf.Materials[0].GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorFactor").EnumerateArray().Select(v => v.GetSingle()).ToArray();

        Assert.All(factor[..3], c => Assert.InRange(c, 0.5f, 0.95f));
        Assert.Equal(1f, factor[3]);
    }

    [Fact]
    public void VertexColorsBecomeColor0WithAWhiteBaseFactor()
    {
        // glTF MULTIPLIES COLOR_0 by baseColorFactor, so a part colour left in would tint
        // every field value by it and a viridis ramp would come out the colour of the part.
        var mesh = MeshPrimitives.Box(10, 10, 10);
        var colors = Enumerable.Range(0, mesh.VertexCount)
            .Select(i => ((float)i / mesh.VertexCount, 0.25f, 0.5f))
            .ToArray();
        var geometries = new List<GltfGeometry>
        {
            new(mesh, "result") { Color = (1f, 0f, 0f), VertexColors = colors },
        };
        var roots = new List<GltfNode> { new("result") { Geometry = 0 } };

        using var stream = new MemoryStream();
        GltfWriter.WriteGlb(geometries, roots, stream);
        var gltf = Gltf.Parse(stream.ToArray());
        gltf.Validate();

        var attributes = gltf.Meshes[0].GetProperty("primitives")[0].GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("COLOR_0", out var color0));
        var factor = gltf.Materials[0].GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorFactor").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal(new[] { 1f, 1f, 1f, 1f }, factor);

        // Every render vertex takes its SOURCE vertex's colour: the flat mesh repeats a
        // corner once per incident triangle, and all copies must agree.
        var read = gltf.ReadVec4(color0.GetInt32());
        Assert.Equal(gltf.Accessors[color0.GetInt32()].GetProperty("count").GetInt32(), read.Length);
        Assert.Contains(read, c => Math.Abs(c.Y - 0.25) < 1e-6 && Math.Abs(c.Z - 0.5) < 1e-6);
        Assert.All(read, c => Assert.Equal(1.0, c.W, 6));
    }

    [Fact]
    public void AWrongVertexColorCountIsRefusedByName()
    {
        var mesh = MeshPrimitives.Box(1, 1, 1);
        var geometries = new List<GltfGeometry>
        {
            new(mesh, "bad") { VertexColors = [(1f, 1f, 1f)] },
        };
        var roots = new List<GltfNode> { new("bad") { Geometry = 0 } };

        using var stream = new MemoryStream();
        var error = Assert.Throws<ArgumentException>(
            () => GltfWriter.WriteGlb(geometries, roots, stream));
        Assert.Contains("bad", error.Message);
        Assert.Contains(mesh.VertexCount.ToString(), error.Message);
    }

    [Fact]
    public void OpacityBelowOneDeclaresAlphaBlending()
    {
        var geometries = new List<GltfGeometry>
        {
            new(MeshPrimitives.Box(1, 1, 1), "glass") { Color = (0.4f, 0.6f, 0.9f), Opacity = 0.35f },
        };
        var roots = new List<GltfNode> { new("glass") { Geometry = 0 } };

        using var stream = new MemoryStream();
        GltfWriter.WriteGlb(geometries, roots, stream);
        var gltf = Gltf.Parse(stream.ToArray());
        gltf.Validate();

        Assert.Equal("BLEND", gltf.Materials[0].GetProperty("alphaMode").GetString());
        Assert.Equal(
            0.35f,
            gltf.Materials[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[3].GetSingle());
    }

    // ---- the JSON container ----

    [Fact]
    public void GltfJsonIsSelfContainedAndCarriesTheSameBufferAsTheGlb()
    {
        var parts = new List<MeshExportPart> { new(MeshPrimitives.Cone(5, 2, 8, 12), "frustum") };
        var glb = WriteGlb(parts);

        using var jsonStream = new MemoryStream();
        var (geometries, roots) = Flat(parts);
        GltfWriter.WriteGltf(geometries, roots, jsonStream);
        using var document = JsonDocument.Parse(jsonStream.ToArray());

        string uri = document.RootElement.GetProperty("buffers")[0].GetProperty("uri").GetString()!;
        Assert.StartsWith("data:application/octet-stream;base64,", uri);
        var embedded = Convert.FromBase64String(uri["data:application/octet-stream;base64,".Length..]);

        // The .gltf's inline buffer IS the .glb's BIN chunk, byte for byte: one writer,
        // two containers, so the two spellings cannot describe different geometry.
        Assert.Equal(Gltf.Parse(glb).Bin, embedded);
    }

    [Fact]
    public void WriteFilePicksTheContainerFromTheExtension()
    {
        var mesh = MeshPrimitives.Box(3, 3, 3);
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            string glb = Path.Combine(directory, "part.glb");
            string gltf = Path.Combine(directory, "part.gltf");
            GltfWriter.WriteFile(mesh, glb);
            GltfWriter.WriteFile(mesh, gltf);

            Assert.Equal("glTF", Encoding.ASCII.GetString(File.ReadAllBytes(glb), 0, 4));
            Assert.Equal('{', File.ReadAllText(gltf).TrimStart()[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- helpers ----

    private static byte[] WriteGlb(IReadOnlyList<MeshExportPart> parts, GltfOptions? options = null)
    {
        using var stream = new MemoryStream();
        GltfWriter.WriteGlb(parts, stream, options);
        return stream.ToArray();
    }

    private static (List<GltfGeometry>, List<GltfNode>) Flat(IReadOnlyList<MeshExportPart> parts)
    {
        var geometries = new List<GltfGeometry>();
        var roots = new List<GltfNode>();
        for (int i = 0; i < parts.Count; i++)
        {
            geometries.Add(new GltfGeometry(parts[i].Mesh, parts[i].Name) { Color = parts[i].Color });
            roots.Add(new GltfNode(parts[i].Name) { Transform = parts[i].Transform, Geometry = i });
        }
        return (geometries, roots);
    }
}

/// <summary>
/// A hand-written glTF 2.0 / GLB reader and structural validator for the tests: the spec's
/// required-property and consistency rules checked by hand, since the project takes no
/// dependency on the reference validator.
/// </summary>
internal sealed class Gltf
{
    public required JsonElement Json { get; init; }
    public required byte[] Bin { get; init; }
    public required uint DeclaredLength { get; init; }
    public required int JsonChunkLength { get; init; }
    public required int BinChunkLength { get; init; }
    public required byte[] JsonPadding { get; init; }
    public required byte[] BinPadding { get; init; }

    public JsonElement[] Nodes => Array("nodes");
    public JsonElement[] Meshes => Array("meshes");
    public JsonElement[] Materials => Array("materials");
    public JsonElement[] Accessors => Array("accessors");
    public JsonElement[] BufferViews => Array("bufferViews");

    public int[] RootNodeIndices =>
        [.. Json.GetProperty("scenes")[Json.GetProperty("scene").GetInt32()]
            .GetProperty("nodes").EnumerateArray().Select(v => v.GetInt32())];

    private JsonElement[] Array(string name) =>
        Json.TryGetProperty(name, out var array) ? [.. array.EnumerateArray()] : [];

    public static Gltf Parse(byte[] bytes)
    {
        Assert.True(bytes.Length >= 20, "a GLB is at least a header plus one chunk header");
        Assert.Equal("glTF", Encoding.ASCII.GetString(bytes, 0, 4));
        uint version = BitConverter.ToUInt32(bytes, 4);
        uint declared = BitConverter.ToUInt32(bytes, 8);
        Assert.Equal(2u, version);
        Assert.Equal((uint)bytes.Length, declared);

        int offset = 12;
        byte[]? jsonBytes = null, binBytes = null;
        byte[] jsonPadding = [], binPadding = [];
        int jsonChunkLength = 0, binChunkLength = 0;

        while (offset < bytes.Length)
        {
            int chunkLength = (int)BitConverter.ToUInt32(bytes, offset);
            uint chunkType = BitConverter.ToUInt32(bytes, offset + 4);
            offset += 8;
            var chunk = bytes[offset..(offset + chunkLength)];
            offset += chunkLength;

            if (chunkType == 0x4E4F534A) // JSON
            {
                jsonChunkLength = chunkLength;
                int end = chunk.Length;
                while (end > 0 && chunk[end - 1] == 0x20)
                    end--;
                jsonBytes = chunk[..end];
                jsonPadding = chunk[end..];
            }
            else if (chunkType == 0x004E4942) // BIN
            {
                binChunkLength = chunkLength;
                binBytes = chunk;
                // Trailing zero padding is indistinguishable from zero data, so the
                // padding length is derived from the accessors below in Validate().
            }
        }

        Assert.NotNull(jsonBytes);
        var document = JsonDocument.Parse(jsonBytes!);
        int used = document.RootElement.TryGetProperty("buffers", out var buffers) && buffers.GetArrayLength() > 0
            ? buffers[0].GetProperty("byteLength").GetInt32()
            : 0;

        return new Gltf
        {
            Json = document.RootElement,
            Bin = binBytes is null ? [] : binBytes[..used],
            DeclaredLength = declared,
            JsonChunkLength = jsonChunkLength,
            BinChunkLength = binChunkLength,
            JsonPadding = jsonPadding,
            BinPadding = binBytes is null ? [] : binBytes[used..],
        };
    }

    /// <summary>The spec rules a consumer relies on, checked by hand.</summary>
    public void Validate()
    {
        Assert.Equal("2.0", Json.GetProperty("asset").GetProperty("version").GetString());

        var views = BufferViews;
        var accessors = Accessors;
        int bufferLength = Bin.Length;

        foreach (var view in views)
        {
            int offset = view.GetProperty("byteOffset").GetInt32();
            int length = view.GetProperty("byteLength").GetInt32();
            Assert.Equal(0, view.GetProperty("buffer").GetInt32());
            Assert.InRange(offset, 0, bufferLength);
            Assert.InRange(offset + length, 0, bufferLength);
        }

        foreach (var accessor in accessors)
        {
            int viewIndex = accessor.GetProperty("bufferView").GetInt32();
            Assert.InRange(viewIndex, 0, views.Length - 1);
            int componentType = accessor.GetProperty("componentType").GetInt32();
            int count = accessor.GetProperty("count").GetInt32();
            string type = accessor.GetProperty("type").GetString()!;
            int componentSize = componentType switch { 5126 or 5125 => 4, 5123 => 2, 5121 => 1, _ => 0 };
            Assert.True(componentSize > 0, $"unknown componentType {componentType}");
            int components = type switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4,
                _ => throw new Xunit.Sdk.XunitException($"unknown accessor type {type}"),
            };

            int viewOffset = views[viewIndex].GetProperty("byteOffset").GetInt32();
            int viewLength = views[viewIndex].GetProperty("byteLength").GetInt32();
            // "the offset of an accessor into the buffer MUST be a multiple of the size
            // of the accessor's component type"
            Assert.Equal(0, viewOffset % componentSize);
            Assert.Equal(count * components * componentSize, viewLength);
        }

        // Node graph: a forest. Every child index valid, no node with two parents, no
        // cycles, every root index valid.
        var nodes = Nodes;
        var parentCount = new int[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].TryGetProperty("children", out var children))
                continue;
            foreach (var child in children.EnumerateArray())
            {
                int index = child.GetInt32();
                Assert.InRange(index, 0, nodes.Length - 1);
                Assert.NotEqual(i, index);
                parentCount[index]++;
            }
        }
        Assert.All(parentCount, c => Assert.InRange(c, 0, 1));
        foreach (int root in RootNodeIndices)
        {
            Assert.InRange(root, 0, nodes.Length - 1);
            Assert.Equal(0, parentCount[root]);
        }
        // Reachability doubles as the acyclicity proof: a cycle would leave its nodes
        // unreachable from any parentless root.
        var reached = new bool[nodes.Length];
        var stack = new Stack<int>(RootNodeIndices);
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            Assert.False(reached[index], "a node was reached twice");
            reached[index] = true;
            if (nodes[index].TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                    stack.Push(child.GetInt32());
            }
        }
        Assert.All(reached, Assert.True);

        // Meshes: primitives non-empty, attributes and material in range, POSITION
        // carrying min/max that actually bound the decoded data, indices in range.
        var materials = Materials;
        foreach (var mesh in Meshes)
        {
            var primitives = mesh.GetProperty("primitives");
            Assert.True(primitives.GetArrayLength() > 0);
            foreach (var primitive in primitives.EnumerateArray())
            {
                Assert.InRange(primitive.GetProperty("material").GetInt32(), 0, materials.Length - 1);
                int position = primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var accessor = accessors[position];
                Assert.True(accessor.TryGetProperty("min", out var min), "POSITION needs min");
                Assert.True(accessor.TryGetProperty("max", out var max), "POSITION needs max");

                var points = ReadVec3(position);
                var lower = min.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var upper = max.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                foreach (var p in points)
                {
                    Assert.InRange(p.X, lower[0], upper[0]);
                    Assert.InRange(p.Y, lower[1], upper[1]);
                    Assert.InRange(p.Z, lower[2], upper[2]);
                }

                foreach (uint index in ReadIndices(primitive.GetProperty("indices").GetInt32()))
                    Assert.InRange(index, 0u, (uint)points.Length - 1);

                if (primitive.TryGetProperty("attributes", out var attributes)
                    && attributes.TryGetProperty("NORMAL", out var normal))
                {
                    Assert.Equal(points.Length, accessors[normal.GetInt32()].GetProperty("count").GetInt32());
                }
            }
        }
    }

    public Vector3d[] ReadVec3(int accessor) =>
        [.. Chunks(accessor, 3).Select(v => new Vector3d(v[0], v[1], v[2]))];

    public (double X, double Y, double Z, double W)[] ReadVec4(int accessor) =>
        [.. Chunks(accessor, 4).Select(v => (v[0], v[1], v[2], v[3]))];

    public uint[] ReadIndices(int accessor)
    {
        var element = Accessors[accessor];
        var view = BufferViews[element.GetProperty("bufferView").GetInt32()];
        int offset = view.GetProperty("byteOffset").GetInt32();
        int count = element.GetProperty("count").GetInt32();
        var indices = new uint[count];
        for (int i = 0; i < count; i++)
            indices[i] = BitConverter.ToUInt32(Bin, offset + i * 4);
        return indices;
    }

    private IEnumerable<double[]> Chunks(int accessor, int components)
    {
        var element = Accessors[accessor];
        var view = BufferViews[element.GetProperty("bufferView").GetInt32()];
        int offset = view.GetProperty("byteOffset").GetInt32();
        int count = element.GetProperty("count").GetInt32();
        for (int i = 0; i < count; i++)
        {
            var values = new double[components];
            for (int c = 0; c < components; c++)
                values[c] = BitConverter.ToSingle(Bin, offset + (i * components + c) * 4);
            yield return values;
        }
    }

    /// <summary>Signed volume of the whole exported scene, triangles walked through the
    /// node transforms — the geometric oracle every fidelity test uses.</summary>
    public double SignedVolume()
    {
        double total = 0;
        foreach (int root in RootNodeIndices)
            total += Accumulate(root, Matrix4d.Identity);
        return total;
    }

    private double Accumulate(int index, in Matrix4d parent)
    {
        var node = Nodes[index];
        var local = node.TryGetProperty("matrix", out var matrix)
            ? FromColumnMajor([.. matrix.EnumerateArray().Select(v => v.GetDouble())])
            : Matrix4d.Identity;
        var world = parent * local;

        double total = 0;
        if (node.TryGetProperty("mesh", out var meshIndex))
        {
            foreach (var primitive in Meshes[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                var points = ReadVec3(primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                var indices = ReadIndices(primitive.GetProperty("indices").GetInt32());
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    var a = world.TransformPoint(points[indices[i]]);
                    var b = world.TransformPoint(points[indices[i + 1]]);
                    var c = world.TransformPoint(points[indices[i + 2]]);
                    total += a.Dot(b.Cross(c)) / 6.0;
                }
            }
        }
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                total += Accumulate(child.GetInt32(), world);
        }
        return total;
    }

    private static Matrix4d FromColumnMajor(double[] m) => new(
        m[0], m[4], m[8], m[12],
        m[1], m[5], m[9], m[13],
        m[2], m[6], m[10], m[14],
        m[3], m[7], m[11], m[15]);
}
