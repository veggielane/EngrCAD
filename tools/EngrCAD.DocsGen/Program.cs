// EngrCAD.DocsGen — executes the C# snippets embedded in docs/**/*.md and renders
// their scenes to PNGs, so every example in the documentation is guaranteed to
// compile, run, and show real output.
//
// Snippet convention (documented in docs/writing-examples.md):
//   ```csharp render:<example-id>     — executed; must define `Scene scene`; rendered
//                                       to <images>/<example-id>.png, which the same
//                                       markdown file must reference.
//   ```csharp animate:<example-id>    — executed; must define `Scene scene` and may
//                                       define `Animation animation` (default: a 4 s
//                                       turntable); rendered to an APNG at
//                                       <images>/<example-id>.png (an APNG IS a PNG,
//                                       so the reference/link rules are unchanged).
//   ```csharp run:<example-id>        — executed for correctness only (no screenshot).
//   ```csharp                         — display-only; ignored by this tool.
// render: fences accept per-snippet render options after the id:
//   style:<points|wireframe|shaded|shaded-edges>   — the global view style
//   section:<x|y|z>,<offset>[;<x|y|z>,<offset>...] — real section planes, e.g.
//                                                    section:z,6 (single cut) or
//                                                    section:x,0;y,0 (quarter cut)
// so docs cutaways use the viewer's actual section planes instead of boolean-cut fakes.
// animate: fences accept style:<name> and frames:<n> (default 24 — mind the docs build
// time and the committed file size; every frame is an offscreen render).
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
var fenceOpen = new Regex(@"^```csharp[ \t]+(render|run|animate):([A-Za-z0-9][A-Za-z0-9-]*)((?:[ \t]+\S+)*)[ \t]*$");
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

        // Optional per-snippet render options after the id (style:<name>, section:<axis>,<offset>;
        // animate: fences take style:<name> and frames:<n>).
        var style = ViewStyle.ShadedWithEdges;
        var sectionAxis = SectionAxis.Z;
        double? sectionOffset = null;
        IReadOnlyList<SectionPlane>? sectionPlanes = null;
        var animationFrames = 24;
        var optionsValid = true;
        foreach (var token in open.Groups[3].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (kind == "run")
            {
                errors.Add($"{file}: snippet '{kind}:{id}' — render options ('{token}') only apply to render:/animate: fences.");
                optionsValid = false;
                break;
            }
            var parts = token.Split(':', 2);
            switch (parts[0])
            {
                case "style" when parts.Length == 2 && TryParseStyle(parts[1], out var s):
                    style = s;
                    break;
                case "section" when kind == "render" && parts.Length == 2 && TryParseSection(parts[1], out var planes):
                    sectionAxis = AxisOf(planes[0]);
                    sectionOffset = planes[0].Offset;
                    sectionPlanes = planes.Count > 1 ? planes : null;
                    break;
                case "frames" when kind == "animate" && parts.Length == 2
                        && int.TryParse(parts[1], out var count) && count is >= 2 and <= 120:
                    animationFrames = count;
                    break;
                default:
                    errors.Add($"{file}: snippet '{kind}:{id}' — unrecognized render option '{token}' "
                             + (kind == "animate"
                                 ? "(animate: fences take style:<points|wireframe|shaded|shaded-edges> "
                                   + "and frames:<2..120>)."
                                 : "(expected style:<points|wireframe|shaded|shaded-edges> or "
                                   + "section:<x|y|z>,<offset> — repeat with ';' for a quarter or octant cut, "
                                   + "e.g. section:x,0;y,0)."));
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
        snippets.Add(new Snippet(id, kind == "render", kind == "animate", file, string.Join('\n', body),
            style, sectionAxis, sectionOffset, sectionPlanes, animationFrames));
    }
}

if (snippets.Count == 0)
{
    Console.Error.WriteLine($"no render:/run: snippets found under {docsRoot} — nothing to verify.");
    return 2;
}

// Every render/animate snippet's markdown must reference the image it produces (an
// APNG is served as .png, so one rule covers both).
foreach (var s in snippets.Where(s => s.Render || s.Animate))
{
    var expectedLink = $"images/{s.Id}.png";
    if (!File.ReadAllText(s.File).Contains(expectedLink, StringComparison.OrdinalIgnoreCase))
        errors.Add($"{s.File}: snippet '{(s.Render ? "render" : "animate")}:{s.Id}' is never shown — "
                 + $"add ![...]({expectedLink}) to the page.");
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
        typeof(Shape).Assembly,
        // The viewer, so a snippet can declare `sectionPlanes`/`camera` (see below) --
        // SectionPlane and CameraState live there.
        typeof(EngrCad).Assembly)
    .AddImports(
        "System", "System.IO", "System.Linq", "System.Collections.Generic",
        "EngrCAD.Core", "EngrCAD.Core.Geometry2", "EngrCAD.Mesh", "EngrCAD.Implicit",
        "EngrCAD.BRep", "EngrCAD.Interop", "EngrCAD.Query", "EngrCAD.Modeling",
        "EngrCAD.Viewer");

var rendered = 0;
var executed = 0;
foreach (var s in snippets)
{
    Console.WriteLine($"[{(s.Render ? "render" : s.Animate ? "animate" : "run   ")}] {s.Id}  ({Path.GetFileName(s.File)})");
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

    if (!s.Render && !s.Animate) continue;

    var scene = state.Variables.LastOrDefault(v => v.Name == "scene")?.Value as Scene;
    if (scene is null)
    {
        errors.Add($"{s.File} ({s.Id}): render snippet must define a variable `scene` of type Scene.");
        continue;
    }

    if (s.Animate)
    {
        // An animation page's fence: the snippet may declare `Animation animation`
        // (and `camera`, the render-fence convention); without one it gets the default
        // 4-second turntable. Output is an APNG at the same images/<id>.png path —
        // an APNG IS a PNG, so DocFX, the link checker and browsers need nothing new.
        if (!TryReadVariable<Animation>(state, "animation", s, errors, out var declaredAnimation)
            || !TryReadVariable<CameraState>(state, "camera", s, errors, out var animateCamera))
            continue;
        var animatePng = Path.Combine(imagesDir, $"{s.Id}.png");
        if (canRender)
        {
            try
            {
                var animation = declaredAnimation
                    ?? new Animation(durationSeconds: 4).With(TurntableTrack.Around(scene));
                animation.RenderApng(scene, animatePng, s.AnimationFrames,
                    width: 640, height: 448, camera: animateCamera, style: s.Style);
                rendered++;
            }
            catch (Exception ex)
            {
                errors.Add($"{s.File} ({s.Id}): RenderApng failed: {ex.Message}");
            }
        }
        else if (!File.Exists(animatePng))
        {
            errors.Add($"{s.File} ({s.Id}): rendering unavailable and no committed APNG at {animatePng} — " +
                       "generate it on a machine with GL and commit it.");
        }
        continue;
    }

    // Optional snippet-declared render inputs, by the same convention as `scene`. Fence
    // options cannot express an oblique plane, a plane LIST or an explicit camera, and
    // adding a mini-language to the info string to carry them would be worse than letting
    // the snippet -- which is real C# with the whole API in scope -- simply declare them.
    // A variable of the right name but the wrong type is an ERROR, never a silent miss:
    // a docs example that quietly ignored its own section plane would be a trap.
    if (!TryReadVariable<IEnumerable<SectionPlane>>(state, "sectionPlanes", s, errors, out var declaredPlanes)
        || !TryReadVariable<SectionCombine?>(state, "sectionCombine", s, errors, out var declaredCombine)
        || !TryReadVariable<CameraState>(state, "camera", s, errors, out var declaredCamera)
        || !TryReadVariable<double?>(state, "explode", s, errors, out var declaredExplode)
        || !TryReadVariable<ConstructionPreviewRequest?>(state, "preview", s, errors, out var declaredPreview))
        continue;

    var planes = declaredPlanes is null ? s.SectionPlanes : [.. declaredPlanes];
    if (planes is { Count: 0 })
    {
        errors.Add($"{s.File} ({s.Id}): `sectionPlanes` is empty -- drop the variable to render unsectioned.");
        continue;
    }

    var pngPath = Path.Combine(imagesDir, $"{s.Id}.png");
    if (canRender)
    {
        try
        {
            // 2x supersampled relative to the display size in the docs pages — the
            // offscreen renderer has no MSAA, so browser downscaling anti-aliases.
            EngrCad.RenderToImage(scene, pngPath, width: 1600, height: 1120, declaredCamera,
                s.Style,
                planes is { Count: > 0 } ? AxisOf(planes[0]) : s.SectionAxis,
                planes is { Count: > 0 } ? planes[0].Offset : s.SectionOffset,
                sectionPlanes: planes,
                sectionCombine: declaredCombine ?? SectionCombine.Intersection,
                preview: declaredPreview,
                explode: declaredExplode ?? 0);
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

// "z,6" is one plane; "x,0;y,0" a quarter cut; "x,0;y,0;z,0" an octant.
static bool TryParseSection(string value, out List<SectionPlane> planes)
{
    planes = [];
    foreach (var clause in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = clause.Split(',', 2);
        if (parts.Length != 2
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double offset))
            return false;
        SectionAxis axis;
        switch (parts[0].ToLowerInvariant())
        {
            case "x": axis = SectionAxis.X; break;
            case "y": axis = SectionAxis.Y; break;
            case "z": axis = SectionAxis.Z; break;
            default: return false;
        }
        planes.Add(SectionPlane.On(axis, offset));
    }
    return planes.Count > 0;
}

/// <summary>Which world axis an axis-aligned section plane sits on. A snippet-declared
/// plane may be oblique, in which case this is only the legacy single-axis hint that
/// `sectionPlanes` overrides anyway -- Z is the harmless default.</summary>
static SectionAxis AxisOf(SectionPlane plane) =>
    plane.Normal == EngrCAD.Core.Vector3d.UnitX ? SectionAxis.X
    : plane.Normal == EngrCAD.Core.Vector3d.UnitY ? SectionAxis.Y
    : SectionAxis.Z;

/// <summary>
/// Reads an optional snippet-declared variable. Returns false only when the variable
/// exists with an incompatible type -- which is reported as an error rather than
/// ignored, so a docs page cannot silently lose the render input it declared.
/// </summary>
static bool TryReadVariable<T>(
    ScriptState<object> state, string name, Snippet snippet, List<string> errors, out T? value)
{
    value = default;
    var variable = state.Variables.LastOrDefault(v => v.Name == name);
    if (variable?.Value is null)
        return true;
    if (variable.Value is T typed)
    {
        value = typed;
        return true;
    }
    errors.Add($"{snippet.File} ({snippet.Id}): `{name}` must be a {typeof(T).Name}, "
             + $"but the snippet declared a {variable.Value.GetType().Name}.");
    return false;
}

internal sealed record Snippet(
    string Id, bool Render, bool Animate, string File, string Code,
    ViewStyle Style, SectionAxis SectionAxis, double? SectionOffset,
    IReadOnlyList<SectionPlane>? SectionPlanes, int AnimationFrames);

namespace EngrCAD.DocsGen
{
    /// <summary>Globals available inside documentation snippets (see docs/writing-examples.md).</summary>
    public sealed class DocsGlobals
    {
        /// <summary>A writable temp directory for snippets that produce files (exports).</summary>
        public string Scratch { get; init; } = "";
    }
}
