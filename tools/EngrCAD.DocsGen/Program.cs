// EngrCAD.DocsGen — executes the C# snippets embedded in docs/**/*.md and renders
// their scenes to PNGs, so every example in the documentation is guaranteed to
// compile, run, and show real output.
//
// Snippet convention (documented in docs/writing-examples.md):
//   ```csharp render:<example-id>     — executed; must define `Scene scene`; rendered
//                                       to <images>/<example-id>.png, which the same
//                                       markdown file must reference.
//   ```csharp run:<example-id>        — executed for correctness only (no screenshot).
//   ```csharp                         — display-only; ignored by this tool.
// render: fences accept per-snippet render options after the id:
//   style:<points|wireframe|shaded|shaded-edges>   — the global view style
//   section:<x|y|z>,<offset>                       — a real section plane, e.g. section:z,6
// so docs cutaways use the viewer's actual section planes instead of boolean-cut fakes.
//
// Exit code: nonzero when any snippet fails to compile/run, defines no scene where one
// is required, reuses an id, or fails to reference its image. When offscreen rendering
// is unavailable (no GL/EGL, e.g. a bare CI runner), rendering is skipped with a
// warning and the committed PNGs are used — but execution failures still fail the run.

using System.Text.RegularExpressions;
using EngrCAD.DocsGen;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

var docsRoot = "docs";
string? imagesDir = null;
var noRender = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--images" when i + 1 < args.Length: imagesDir = args[++i]; break;
        case "--no-render": noRender = true; break;
        default: docsRoot = args[i]; break;
    }
}

docsRoot = Path.GetFullPath(docsRoot);
if (!Directory.Exists(docsRoot))
{
    Console.Error.WriteLine($"docs root not found: {docsRoot}");
    return 2;
}

imagesDir = Path.GetFullPath(imagesDir ?? Path.Combine(docsRoot, "examples", "images"));
Directory.CreateDirectory(imagesDir);

var scratch = Path.Combine(Path.GetTempPath(), "engrcad-docsgen");
Directory.CreateDirectory(scratch);

var canRender = !noRender && EngrCad.CanRenderToImage;
if (!canRender)
{
    Console.WriteLine(noRender
        ? "WARNING: rendering disabled (--no-render); committed PNGs will be used."
        : "WARNING: offscreen rendering unavailable on this machine; committed PNGs will be used. "
          + "Snippets are still executed and failures still fail the build.");
}

// ---- collect snippets ------------------------------------------------------------
var fenceOpen = new Regex(@"^```csharp[ \t]+(render|run):([A-Za-z0-9][A-Za-z0-9-]*)((?:[ \t]+\S+)*)[ \t]*$");
var snippets = new List<Snippet>();
var errors = new List<string>();
var seenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

var mdFiles = Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}_site{Path.DirectorySeparatorChar}")
             && !f.Contains($"{Path.DirectorySeparatorChar}api{Path.DirectorySeparatorChar}")
             && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();

foreach (var file in mdFiles)
{
    var lines = File.ReadAllLines(file);
    var inOuterFence = false; // ````-fenced blocks quote fences verbatim (writing-examples.md)
    for (var i = 0; i < lines.Length; i++)
    {
        if (lines[i].StartsWith("````", StringComparison.Ordinal))
        {
            inOuterFence = !inOuterFence;
            continue;
        }
        if (inOuterFence) continue;

        var open = fenceOpen.Match(lines[i]);
        if (!open.Success) continue;

        var kind = open.Groups[1].Value;
        var id = open.Groups[2].Value;

        // Optional per-snippet render options after the id (style:<name>, section:<axis>,<offset>).
        var style = ViewStyle.ShadedWithEdges;
        var sectionAxis = SectionAxis.Z;
        double? sectionOffset = null;
        var optionsValid = true;
        foreach (var token in open.Groups[3].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (kind != "render")
            {
                errors.Add($"{file}: snippet '{kind}:{id}' — render options ('{token}') only apply to render: fences.");
                optionsValid = false;
                break;
            }
            var parts = token.Split(':', 2);
            switch (parts[0])
            {
                case "style" when parts.Length == 2 && TryParseStyle(parts[1], out var s):
                    style = s;
                    break;
                case "section" when parts.Length == 2 && TryParseSection(parts[1], out var axis, out var offset):
                    sectionAxis = axis;
                    sectionOffset = offset;
                    break;
                default:
                    errors.Add($"{file}: snippet '{kind}:{id}' — unrecognized render option '{token}' "
                             + "(expected style:<points|wireframe|shaded|shaded-edges> or section:<x|y|z>,<offset>).");
                    optionsValid = false;
                    break;
            }
        }

        var body = new List<string>();
        var closed = false;
        for (var j = i + 1; j < lines.Length; j++)
        {
            if (lines[j].TrimEnd() == "```") { closed = true; i = j; break; }
            body.Add(lines[j]);
        }

        if (!closed)
        {
            errors.Add($"{file}: unclosed snippet fence '{kind}:{id}'");
            break;
        }

        if (seenIds.TryGetValue(id, out var firstFile))
        {
            errors.Add($"{file}: duplicate example id '{id}' (first used in {firstFile})");
            continue;
        }

        if (!optionsValid)
            continue;

        seenIds[id] = file;
        snippets.Add(new Snippet(id, kind == "render", file, string.Join('\n', body),
            style, sectionAxis, sectionOffset));
    }
}

if (snippets.Count == 0)
{
    Console.Error.WriteLine($"no render:/run: snippets found under {docsRoot} — nothing to verify.");
    return 2;
}

// Every render snippet's markdown must reference the image it produces.
foreach (var s in snippets.Where(s => s.Render))
{
    var expectedLink = $"images/{s.Id}.png";
    if (!File.ReadAllText(s.File).Contains(expectedLink, StringComparison.OrdinalIgnoreCase))
        errors.Add($"{s.File}: snippet 'render:{s.Id}' is never shown — add ![...]({expectedLink}) to the page.");
}

// ---- execute ---------------------------------------------------------------------
var options = ScriptOptions.Default
    .AddReferences(
        typeof(EngrCAD.Core.Vector3d).Assembly,
        typeof(EngrCAD.Mesh.HalfEdgeMesh).Assembly,
        typeof(EngrCAD.Implicit.Sdf).Assembly,
        typeof(EngrCAD.BRep.BrepSolid).Assembly,
        typeof(EngrCAD.Interop.BrepBoolean).Assembly,
        typeof(EngrCAD.Query.SpatialCollection<>).Assembly,
        typeof(Shape).Assembly)
    .AddImports(
        "System", "System.IO", "System.Linq", "System.Collections.Generic",
        "EngrCAD.Core", "EngrCAD.Mesh", "EngrCAD.Implicit", "EngrCAD.BRep",
        "EngrCAD.Interop", "EngrCAD.Query", "EngrCAD.Modeling");

var rendered = 0;
var executed = 0;
foreach (var s in snippets)
{
    Console.WriteLine($"[{(s.Render ? "render" : "run   ")}] {s.Id}  ({Path.GetFileName(s.File)})");
    ScriptState<object>? state = null;
    try
    {
        state = await CSharpScript.RunAsync(s.Code, options, new DocsGlobals { Scratch = scratch }, typeof(DocsGlobals));
        executed++;
    }
    catch (CompilationErrorException ex)
    {
        errors.Add($"{s.File} ({s.Id}): snippet failed to COMPILE:\n  {string.Join("\n  ", ex.Diagnostics)}");
        continue;
    }
    catch (Exception ex)
    {
        errors.Add($"{s.File} ({s.Id}): snippet THREW at run time: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    if (!s.Render) continue;

    var scene = state.Variables.LastOrDefault(v => v.Name == "scene")?.Value as Scene;
    if (scene is null)
    {
        errors.Add($"{s.File} ({s.Id}): render snippet must define a variable `scene` of type Scene.");
        continue;
    }

    var pngPath = Path.Combine(imagesDir, $"{s.Id}.png");
    if (canRender)
    {
        try
        {
            // 2x supersampled relative to the display size in the docs pages — the
            // offscreen renderer has no MSAA, so browser downscaling anti-aliases.
            EngrCad.RenderToImage(scene, pngPath, width: 1600, height: 1120, camera: null,
                s.Style, s.SectionAxis, s.SectionOffset);
            rendered++;
        }
        catch (Exception ex)
        {
            errors.Add($"{s.File} ({s.Id}): RenderToImage failed: {ex.Message}");
        }
    }
    else if (!File.Exists(pngPath))
    {
        errors.Add($"{s.File} ({s.Id}): rendering unavailable and no committed PNG at {pngPath} — " +
                   "generate the image on a machine with GL and commit it.");
    }
}

// ---- report ----------------------------------------------------------------------
Console.WriteLine($"\n{snippets.Count} snippets: {executed} executed, {rendered} rendered" +
                  (canRender ? "" : " (rendering skipped)") + $", {errors.Count} error(s).");
foreach (var e in errors)
    Console.Error.WriteLine($"ERROR: {e}");

return errors.Count == 0 ? 0 : 1;

// ---- render-option parsing (the CLI spellings of EngrCad.Run's --render-style/--section) ----
static bool TryParseStyle(string value, out ViewStyle style)
{
    switch (value.ToLowerInvariant())
    {
        case "points": style = ViewStyle.Points; return true;
        case "wireframe": style = ViewStyle.Wireframe; return true;
        case "shaded": style = ViewStyle.Shaded; return true;
        case "shaded-edges": style = ViewStyle.ShadedWithEdges; return true;
        default: style = default; return false;
    }
}

static bool TryParseSection(string value, out SectionAxis axis, out double offset)
{
    axis = default;
    offset = 0;
    var parts = value.Split(',', 2);
    if (parts.Length != 2
        || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out offset))
        return false;
    switch (parts[0].ToLowerInvariant())
    {
        case "x": axis = SectionAxis.X; return true;
        case "y": axis = SectionAxis.Y; return true;
        case "z": axis = SectionAxis.Z; return true;
        default: return false;
    }
}

internal sealed record Snippet(
    string Id, bool Render, string File, string Code,
    ViewStyle Style, SectionAxis SectionAxis, double? SectionOffset);

namespace EngrCAD.DocsGen
{
    /// <summary>Globals available inside documentation snippets (see docs/writing-examples.md).</summary>
    public sealed class DocsGlobals
    {
        /// <summary>A writable temp directory for snippets that produce files (exports).</summary>
        public string Scratch { get; init; } = "";
    }
}
