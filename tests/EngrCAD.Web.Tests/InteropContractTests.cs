using System.Text.Json;
using System.Text.RegularExpressions;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// The one seam in this project with no compiler behind it: <c>FrameDescription</c> is
/// serialized by Blazor and read by <c>engrcad-gl.js</c> as plain JSON, so a renamed
/// property fails silently — the uniform is simply never set and the frame looks subtly
/// wrong, or the draw silently does nothing.
///
/// <para>These tests read the JavaScript itself and hold it to the serialized shape.
/// They are cheap to keep working (add a property, add it to the sample frame) and the
/// alternative is finding the mismatch by staring at a render.</para>
/// </summary>
public class InteropContractTests
{
    /// <summary>Blazor's JS interop serializer settings.</summary>
    private static readonly JsonSerializerOptions Interop = new(JsonSerializerDefaults.Web);

    /// <summary>A frame exercising every property either side knows about.</summary>
    private static FrameDescription SampleFrame() => new()
    {
        Clear = [0.1f, 0.2f, 0.3f],
        Shared = new Dictionary<string, object> { ["uView"] = new float[16] },
        Draws =
        [
            new DrawCall
            {
                Program = "mesh",
                Geometry = "part0",
                First = 0,
                Count = 6,
                Mode = "points",
                Blend = true,
                DepthWrite = false,
                DepthTest = false,
                Cull = false,
                PolygonOffset = [1f, 1f],
                Viewport = [0, 0, 10, 10],
                ClearDepth = true,
                Uniforms = new Dictionary<string, object> { ["uColor"] = new[] { 1f, 0f, 0f } },
            },
        ],
    };

    [Fact]
    public void JavaScriptReadsOnlyPropertiesTheFrameSerializes()
    {
        string js = ReadInteropModule();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(SampleFrame(), Interop));
        var root = document.RootElement;
        var call = root.GetProperty("draws")[0];

        // Both objects' property names are pooled: the JS names its parameter `frame` and
        // its loop variable `call`, but a lookup on the wrong one is a JS bug, not a
        // contract break, and the contract is what this test is for.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            known.Add(property.Name);
        foreach (var property in call.EnumerateObject())
            known.Add(property.Name);

        var accessed = Regex.Matches(js, @"\b(?:frame|call)\.([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(accessed);
        var unknown = accessed.Except(known).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            $"engrcad-gl.js reads properties the frame does not serialize: {string.Join(", ", unknown)}. "
            + $"Serialized properties: {string.Join(", ", known.OrderBy(n => n, StringComparer.Ordinal))}");
    }

    [Fact]
    public void UniformValuesSerializeAsTheShapesTheInteropDispatchesOn()
    {
        // setUniform switches on `typeof value` and, for arrays, on value.length. A float
        // must therefore arrive as a JSON number and a vector as a JSON array of the right
        // length -- boxing them as objects would silently take the default branch.
        var uniforms = new Dictionary<string, object>
        {
            ["scalar"] = 1.5f,
            ["vec3"] = new[] { 1f, 2f, 3f },
            ["mat4"] = new float[16],
            ["int"] = new IntUniform(2),
            ["vec4s"] = new Vec4ArrayUniform([1f, 2f, 3f, 4f]),
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(uniforms, Interop));

        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("scalar").ValueKind);
        Assert.Equal(3, document.RootElement.GetProperty("vec3").GetArrayLength());
        Assert.Equal(16, document.RootElement.GetProperty("mat4").GetArrayLength());

        // The typed markers: an int uniform serializes as {"int": n} (uniform1i) and a
        // vec4 array as {"vec4": [...]} (uniform4fv) -- the property names are what the
        // JavaScript dispatches on, so a rename here is a silent no-op there.
        var intMarker = document.RootElement.GetProperty("int");
        Assert.Equal(JsonValueKind.Number, intMarker.GetProperty("int").ValueKind);
        Assert.Equal(2, intMarker.GetProperty("int").GetInt32());
        Assert.Equal(4, document.RootElement.GetProperty("vec4s").GetProperty("vec4").GetArrayLength());
    }

    [Fact]
    public void EveryDepthClearIsUnmaskedFirst()
    {
        // HARD-WON, and the one GL rule this interop can break silently: glClear of the
        // DEPTH buffer is gated by the depth MASK. The module applies per-draw state and
        // never resets it, so a frame whose LAST draw sets depthWrite false -- the
        // annotation overlay, a translucent fill, the undeformed ghost pass -- leaves the
        // mask off, and the next frame's clear then keeps the PREVIOUS depth buffer.
        // Every model fragment fails LESS against its own stale depth and the model
        // disappears, leaving only the draws that disable the depth test. Measured on the
        // demo with the overlay on and the cube off: 32 374 -> 786 lit pixels, and the
        // ?report beacon reported it as a healthy-looking `annotationPixels=34317`.
        //
        // There is no compiler and no type behind this: it is one `gl.depthMask(true)`
        // that must precede each clear, so the guard reads the source, the way the
        // property contract above does.
        string js = ReadInteropModule();
        var clears = Regex.Matches(js, @"gl\.clear\([^)]*DEPTH_BUFFER_BIT[^)]*\)");
        Assert.NotEmpty(clears);
        foreach (Match clear in clears)
        {
            // The nearest preceding depth-mask write must enable writes. Nearest, not
            // "any": a `depthMask(false)` between an enabling one and the clear puts the
            // mask back off, which is exactly the bug.
            var masks = Regex.Matches(js[..clear.Index], @"gl\.depthMask\(([^)]*)\)");
            Assert.True(masks.Count > 0,
                "A depth clear in engrcad-gl.js has no gl.depthMask() before it: "
                + Context(js, clear.Index));
            string argument = masks[^1].Groups[1].Value.Trim();
            Assert.True(argument == "true",
                $"The depth clear at '{Context(js, clear.Index)}' inherits gl.depthMask({argument}). "
                + "glClear is masked by it, so the clear must be preceded by gl.depthMask(true) "
                + "-- a clear is not a draw and must not inherit a draw's mask.");
        }
    }

    /// <summary>The source line a match sits on, for a failure message that names WHERE
    /// rather than only what.</summary>
    private static string Context(string js, int index)
    {
        int start = js.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        int end = js.IndexOf('\n', index);
        return js[start..(end < 0 ? js.Length : end)].Trim();
    }

    [Fact]
    public void ShaderSourcesAreAscii()
    {
        // HARD-WON: an em dash in a shader comment made ANGLE's translator reject the
        // whole shader; the compile exception aborted init before the other programs were
        // built and the entire desktop viewport rendered black. The browser compiles
        // these same strings, so the rule holds here too -- and this is the front end
        // where a rejected shader is hardest to notice.
        string[] sources =
        [
            Viewer.ViewerShaders.MeshVertex, Viewer.ViewerShaders.MeshFragment,
            Viewer.ViewerShaders.LineVertex, Viewer.ViewerShaders.LineFragment,
            Viewer.ViewerShaders.PointVertex, Viewer.ViewerShaders.PointFragment,
            Viewer.ViewerShaders.BackgroundVertex, Viewer.ViewerShaders.BackgroundFragment,
            Viewer.ViewerShaders.SectionClip, Viewer.ViewerShaders.Header(es: true),
        ];

        foreach (string source in sources)
        {
            for (int i = 0; i < source.Length; i++)
            {
                Assert.True(source[i] <= 127,
                    $"Non-ASCII character U+{(int)source[i]:X4} in shader source near: "
                    + source.Substring(Math.Max(0, i - 40), Math.Min(80, source.Length - Math.Max(0, i - 40))));
            }
        }
    }

    /// <summary>Reads the interop module from the repository rather than the test output:
    /// a Razor class library's wwwroot is a static web asset, not a copied file.</summary>
    private static string ReadInteropModule()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EngrCAD.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string path = Path.Combine(directory!.FullName, "src", "EngrCAD.Web", "wwwroot", "engrcad-gl.js");
        Assert.True(File.Exists(path), $"Interop module not found at {path}");
        return File.ReadAllText(path);
    }
}
