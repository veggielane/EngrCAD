using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using ModelContextProtocol.Protocol;

namespace EngrCAD.Mcp;

/// <summary>
/// The MCP tool layer: one method per tool, each returning the protocol's
/// <see cref="CallToolResult"/> so tests can invoke them directly — no client, no
/// transport, no process. Everything the server exposes is implemented here; the
/// server file only names, describes, and wires these methods.
/// <para>Failures are returned as <c>IsError</c> results with a human-readable
/// message rather than thrown: an assistant should be told "no part named X, here are
/// the names" and carry on, not receive a protocol-level error.</para>
/// </summary>
public sealed class SceneTools(SceneSession session)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>One offscreen render at a time, process-wide. Parallel EGL pbuffer
    /// creation is flaky (the reason the viewer's GL tests share an xUnit collection),
    /// and an assistant can legitimately have two screenshot calls in flight.</summary>
    private static readonly Lock RenderGate = new();

    /// <summary>The session these tools read; exposed for hosts that also serve the
    /// scene as a resource.</summary>
    public SceneSession Session { get; } = session;

    // ---- tools ----

    /// <summary>The scene's tabs with their cheap contents counts (no meshing).</summary>
    public CallToolResult ListTabs()
    {
        var scene = Session.Scene;
        var tabs = new JsonArray();
        foreach (var tab in scene.Tabs)
        {
            tabs.Add(new JsonObject
            {
                ["name"] = tab.Name,
                ["parts"] = tab.Parts.Count,
                ["assemblies"] = tab.Assemblies.Count,
                ["distinctParts"] = tab.AllParts.Count(),
                ["instances"] = tab.Instances().Count,
            });
        }
        return Ok(new JsonObject { ["generation"] = Session.Generation, ["tabs"] = tabs });
    }

    /// <summary>
    /// Every distinct part, with the facts that cost nothing: name, tab, geometry kind,
    /// how many times it is instanced and under which occurrence paths, display mode,
    /// colour, whether it has an exact B-Rep route, and whether it carries annotations
    /// or a construction tree. Deliberately no volumes or face counts — those need the
    /// display mesh, which is what <c>describe_part</c> is for.
    /// </summary>
    public CallToolResult ListParts(
        [Description("Only list this tab's parts (omit for the whole scene).")] string? tab = null)
    {
        var scene = Session.Scene;
        if (tab is not null && Session.FindTab(tab) is null)
            return Error(UnknownTab(tab));

        // Occurrence paths per part, from the flattening every viewer/exporter uses.
        var paths = new Dictionary<Part, List<string>>();
        foreach (var instance in scene.AllInstances)
        {
            if (!paths.TryGetValue(instance.Part, out var list))
                paths[instance.Part] = list = [];
            list.Add(instance.Path);
        }

        var parts = new JsonArray();
        foreach (var candidate in scene.Tabs)
        {
            if (tab is not null && candidate.Name != tab)
                continue;
            foreach (var part in candidate.AllParts)
            {
                var placements = paths.TryGetValue(part, out var list) ? list : [];
                var entry = new JsonObject
                {
                    ["name"] = part.Name,
                    ["tab"] = candidate.Name,
                    ["kind"] = KindOf(part),
                    ["displayMode"] = part.DisplayMode.ToString().ToLowerInvariant(),
                    ["annotations"] = part.Annotations.Count,
                    ["hasConstructionTree"] = part.History is not null || part.Geometry is Shape,
                    ["exactBrep"] = IsBrepRepresentable(part),
                    ["instances"] = placements.Count,
                    ["paths"] = new JsonArray([.. placements.Select(p => (JsonNode)p)]),
                };
                if (part.Color is { } color)
                    entry["color"] = new JsonArray(color.R, color.G, color.B);
                parts.Add(entry);
            }
        }
        return Ok(new JsonObject { ["generation"] = Session.Generation, ["parts"] = parts });
    }

    /// <summary>
    /// One part in full: the viewer's properties-panel facts (kind, faces, closed,
    /// volume, area, bounds, size, position) plus its annotations and — unless
    /// <paramref name="constructionTree"/> is false — how it was built, straight from
    /// <see cref="Part.ConstructionTree"/>. <b>This is the tool that meshes</b>, and
    /// only the named part.
    /// </summary>
    public CallToolResult DescribePart(
        [Description("Part name, as reported by list_parts; \"tab/part\" also works.")] string name,
        [Description("Tab to look in, when the same name exists in several.")] string? tab = null,
        [Description("Include the construction tree (how the part was built). Default true.")]
        bool constructionTree = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error("describe_part needs a part name; call list_parts to see them.");
        if (Session.FindPart(name, tab) is not { } found)
            return Error(UnknownPart(name, tab));
        var (owningTab, part) = found;

        HalfEdgeMesh mesh;
        try
        {
            mesh = Session.MeshOf(part);   // the lazy seam: this part only
        }
        catch (Exception e)
        {
            return Error($"Part '{part.Name}' could not be meshed: {e.GetType().Name}: {e.Message}");
        }

        var local = mesh.ComputeBounds();
        var world = part.Bounds(Session.Quality);
        var result = new JsonObject
        {
            ["generation"] = Session.Generation,
            ["name"] = part.Name,
            ["tab"] = owningTab.Name,
            ["kind"] = KindOf(part),
            ["displayMode"] = part.DisplayMode.ToString().ToLowerInvariant(),
            ["exactBrep"] = IsBrepRepresentable(part),
            ["faces"] = mesh.FaceCount,
            ["vertices"] = mesh.VertexCount,
            ["closed"] = mesh.IsClosed,
            ["volume"] = mesh.IsClosed ? mesh.Volume() : null,
            ["area"] = mesh.SurfaceArea(),
            ["bounds"] = BoundsJson(world),
            ["localBounds"] = BoundsJson(local),
            ["position"] = PointJson(part.Transform.TransformPoint(Vector3d.Zero)),
        };

        var instancePaths = new JsonArray();
        foreach (var instance in Session.Scene.AllInstances)
        {
            if (ReferenceEquals(instance.Part, part))
                instancePaths.Add(instance.Path);
        }
        result["paths"] = instancePaths;

        if (part.Annotations.Count > 0)
        {
            // Resolving re-runs the selectors against the current geometry, so the
            // measured values are the model's, not a stale cache. A selector broken by
            // an edit becomes a diagnostic here, exactly as it becomes a status message
            // in the viewer.
            bool resolved = part.TryResolveAnnotations(out var list, out string? annotationError);
            var annotations = new JsonArray();
            for (int i = 0; i < part.Annotations.Count; i++)
            {
                var entry = new JsonObject { ["type"] = part.Annotations[i].GetType().Name };
                if (resolved && i < list.Count)
                {
                    entry["text"] = list[i].Text;
                    entry["value"] = list[i].Value;
                }
                annotations.Add(entry);
            }
            result["annotations"] = annotations;
            if (!resolved)
                result["annotationsError"] = annotationError;
        }

        if (constructionTree && part.ConstructionTree() is { } tree)
            result["constructionTree"] = ConstructionJson(tree);

        return Ok(result);
    }

    /// <summary>
    /// Renders the scene (or one tab, or one part) headlessly and returns the PNG as
    /// MCP image content — the tool that lets an assistant actually see the model.
    /// </summary>
    /// <param name="view">A standard view: iso (default), front, back, left, right,
    /// top, bottom — or "default" for the renderer's own auto-framed iso.</param>
    /// <param name="style">Global view style: shaded-edges (default), shaded,
    /// wireframe, points.</param>
    /// <param name="tab">Render only this tab's instances.</param>
    /// <param name="part">Render only this part.</param>
    /// <param name="sectionAxis">x, y or z — with <paramref name="sectionOffset"/>,
    /// cuts the model open along that axis.</param>
    /// <param name="sectionOffset">Offset of the section plane along its axis;
    /// omitting it means no section.</param>
    /// <param name="width">Image width in pixels (default 1280).</param>
    /// <param name="height">Image height in pixels (default 800).</param>
    /// <param name="ambientOcclusion">Baked ambient occlusion (default on).</param>
    public CallToolResult Screenshot(
        [Description("Standard view: iso (default), front, back, left, right, top, bottom.")]
        string? view = null,
        [Description("Display style: shaded-edges (default), shaded, wireframe, points.")]
        string? style = null,
        [Description("Render only this tab.")] string? tab = null,
        [Description("Render only this part.")] string? part = null,
        [Description("Section-plane axis: x, y, or z. Requires sectionOffset.")]
        string? sectionAxis = null,
        [Description("Position of the section plane along its axis. Omit for no section.")]
        double? sectionOffset = null,
        [Description("Image width in pixels, 16-4096 (default 1280).")] int width = 1280,
        [Description("Image height in pixels, 16-4096 (default 800).")] int height = 800,
        [Description("Baked ambient occlusion — depth shading in pockets and bores (default true).")]
        bool ambientOcclusion = true)
    {
        // Arguments are validated before the GL check so a typo is reported as a typo
        // even on a machine with no GPU (and so the validation is testable there).
        if (width is < 16 or > 4096 || height is < 16 or > 4096)
            return Error("width and height must be between 16 and 4096 pixels.");
        if (!TryResolveStyle(style, out var viewStyle, out string? styleError))
            return Error(styleError);
        if (view is not null && !view.Equals("default", StringComparison.OrdinalIgnoreCase)
            && StandardViews.DirectionFor(view) is null)
            return Error($"Unknown view '{view}' — use one of: {string.Join(", ", StandardViews.Names)}.");
        if (!TryResolveSection(sectionAxis, sectionOffset, out var axis, out double? offset, out string? sectionError))
            return Error(sectionError);
        if (!TryResolveInstances(tab, part, out var instances, out string? scopeError))
            return Error(scopeError);
        if (instances.Count == 0)
            return Error("Nothing to render: the scene (or the requested tab/part) has no parts.");

        if (!EngrCad.CanRenderToImage)
            return Error(
                "Offscreen rendering is not available on this machine "
                + $"({OffscreenRenderer.UnavailableReason ?? "no GL/EGL context"}). "
                + "Every other tool still works; run the model with --view to see it interactively.");

        CameraState? camera;
        try
        {
            PrepareGeometry(tab, part, instances);
            camera = StandardViews.For(view ?? "iso", instances, Session.Quality);
        }
        catch (Exception e)
        {
            return Error($"{e.GetType().Name}: {e.Message}");
        }

        // OffscreenRenderer encodes straight to a file (its PNG writer is internal to
        // the viewer), so the bytes come back via a temp file that is deleted at once.
        string temporary = Path.Combine(Path.GetTempPath(), $"engrcad-mcp-{Guid.NewGuid():N}.png");
        byte[] png;
        try
        {
            lock (RenderGate)
            {
                OffscreenRenderer.RenderToImage(instances, temporary, width, height, camera,
                    furniture: true, viewStyle, axis, offset, ambientOcclusion);
            }
            png = File.ReadAllBytes(temporary);
        }
        catch (Exception e)
        {
            return Error($"Rendering failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }

        string scope = part is not null ? $"part '{part}'" : tab is not null ? $"tab '{tab}'" : "scene";
        string sectionText = offset is { } o
            ? string.Create(CultureInfo.InvariantCulture, $", section {axis.ToString().ToLowerInvariant()} at {o:G6}")
            : "";
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(png, "image/png"),
                new TextContentBlock
                {
                    Text = $"{scope}, {view ?? "iso"} view, {viewStyle.ToString().ToLowerInvariant()}"
                         + $"{sectionText} — {width}x{height}, {instances.Count} instance(s).",
                },
            ],
        };
    }

    /// <summary>
    /// Writes the scene to a file the caller names: <c>.step</c> (exact B-Rep, one file
    /// per part), <c>.stl</c>/<c>.obj</c> (meshes, instances merged with their
    /// transforms), or <c>.png</c> (a render).
    /// </summary>
    public CallToolResult Export(
        [Description("Destination file path; the extension picks the format (.step, .stl, .obj, .png).")]
        string path,
        [Description("Export only this tab (omit for the whole scene).")] string? tab = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Error("export needs a file path ending in .step, .stl, .obj, or .png.");
        if (!TryResolveInstances(tab, part: null, out var instances, out string? scopeError))
            return Error(scopeError);
        if (instances.Count == 0)
            return Error("Nothing to export: the scene (or the requested tab) has no parts.");

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".png")
        {
            return EngrCad.CanRenderToImage
                ? ExportPng(path, instances)
                : Error("Offscreen rendering is not available on this machine "
                      + $"({OffscreenRenderer.UnavailableReason ?? "no GL/EGL context"}); "
                      + "export .step, .stl, or .obj instead.");
        }

        try
        {
            var quality = Session.Quality;
            switch (extension)
            {
                case ".stl":
                    StlWriter.WriteFile([.. instances.Select(i => (i.Part.GetMesh(quality), i.World))], path);
                    return Ok(new JsonObject
                    {
                        ["wrote"] = Path.GetFullPath(path),
                        ["format"] = "binary STL",
                        ["instances"] = instances.Count,
                    });

                case ".obj":
                    WriteMergedObj(instances, path, quality);
                    return Ok(new JsonObject
                    {
                        ["wrote"] = Path.GetFullPath(path),
                        ["format"] = "OBJ (merged)",
                        ["instances"] = instances.Count,
                    });

                case ".step" or ".stp":
                    return ExportStep(path, instances);

                default:
                    return Error(
                        $"Unsupported export format '{extension}' — use .step, .stl, .obj, or .png.");
            }
        }
        catch (Exception e)
        {
            return Error($"Export failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Re-invokes the design program's scene factory and swaps the result in —
    /// what a <c>dotnet watch</c> hot reload does to the viewer. A factory that throws
    /// leaves the previous scene untouched and reports the error.</summary>
    public CallToolResult Reload()
    {
        try
        {
            var scene = Session.Reload();
            return Ok(new JsonObject
            {
                ["generation"] = Session.Generation,
                ["tabs"] = scene.Tabs.Count,
                ["parts"] = scene.AllParts.Count(),
                ["status"] = "reloaded",
            });
        }
        catch (Exception e)
        {
            return Error($"The model failed to rebuild (keeping the previous scene): "
                       + $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The whole document as one JSON object — the <c>engrcad://scene</c>
    /// resource. Cheap facts only, same data as list_tabs + list_parts.</summary>
    public string SceneJson()
    {
        var tabs = new JsonArray();
        foreach (var tab in Session.Scene.Tabs)
        {
            var parts = new JsonArray();
            foreach (var part in tab.AllParts)
            {
                parts.Add(new JsonObject
                {
                    ["name"] = part.Name,
                    ["kind"] = KindOf(part),
                    ["exactBrep"] = IsBrepRepresentable(part),
                });
            }
            tabs.Add(new JsonObject
            {
                ["name"] = tab.Name,
                ["instances"] = tab.Instances().Count,
                ["parts"] = parts,
            });
        }
        return new JsonObject
        {
            ["generation"] = Session.Generation,
            ["tabs"] = tabs,
        }.ToJsonString(Json);
    }

    // ---- scope + option resolution ----

    /// <summary>Meshes what a render/export is about to consume: the whole scene
    /// through <see cref="Scene.PreMesh"/> when the scope IS the whole scene (so it
    /// inherits that routine's parallelism), otherwise only the scoped instances.</summary>
    private void PrepareGeometry(string? tab, string? part, IReadOnlyList<PartInstance> instances)
    {
        if (tab is null && part is null)
            Session.PreMesh();
        else
            Session.PreMesh(instances);
    }

    private bool TryResolveInstances(
        string? tab, string? part, out IReadOnlyList<PartInstance> instances, out string? error)
    {
        var scene = Session.Scene;
        if (part is not null)
        {
            if (Session.FindPart(part, tab) is not { } found)
            {
                instances = [];
                error = UnknownPart(part, tab);
                return false;
            }
            var matches = scene.AllInstances.Where(i => ReferenceEquals(i.Part, found.Part)).ToList();
            // A part that is in a tab but placed nowhere still renders at its own transform.
            instances = matches.Count > 0
                ? matches
                : [new PartInstance(found.Part, found.Part.Transform, found.Part.Name)];
            error = null;
            return true;
        }
        if (tab is not null)
        {
            if (Session.FindTab(tab) is not { } found)
            {
                instances = [];
                error = UnknownTab(tab);
                return false;
            }
            instances = found.Instances();
            error = null;
            return true;
        }
        instances = [.. scene.AllInstances];
        error = null;
        return true;
    }

    private static bool TryResolveStyle(string? style, out ViewStyle result, out string? error)
    {
        error = null;
        switch (style?.ToLowerInvariant())
        {
            case null or "" or "shaded-edges" or "shadedwithedges": result = ViewStyle.ShadedWithEdges; return true;
            case "shaded": result = ViewStyle.Shaded; return true;
            case "wireframe": result = ViewStyle.Wireframe; return true;
            case "points": result = ViewStyle.Points; return true;
            default:
                result = ViewStyle.ShadedWithEdges;
                error = $"Unknown style '{style}' — use shaded-edges, shaded, wireframe, or points.";
                return false;
        }
    }

    private static bool TryResolveSection(
        string? axisName, double? offset, out SectionAxis axis, out double? resolved, out string? error)
    {
        axis = SectionAxis.Z;
        resolved = null;
        error = null;
        if (axisName is null && offset is null)
            return true;
        if (offset is null)
        {
            error = "sectionAxis needs a sectionOffset (the plane's position along that axis).";
            return false;
        }
        switch (axisName?.ToLowerInvariant())
        {
            case null or "" or "z": axis = SectionAxis.Z; break;
            case "x": axis = SectionAxis.X; break;
            case "y": axis = SectionAxis.Y; break;
            default:
                error = $"Unknown sectionAxis '{axisName}' — use x, y, or z.";
                return false;
        }
        resolved = offset;
        return true;
    }

    // ---- export helpers ----

    private CallToolResult ExportPng(string path, IReadOnlyList<PartInstance> instances)
    {
        try
        {
            Session.PreMesh(instances);
            var camera = StandardViews.For("iso", instances, Session.Quality);
            lock (RenderGate)
                OffscreenRenderer.RenderToImage(instances, path, 1280, 800, camera);
            return Ok(new JsonObject
            {
                ["wrote"] = Path.GetFullPath(path),
                ["format"] = "PNG",
                ["instances"] = instances.Count,
            });
        }
        catch (Exception e)
        {
            return Error($"Render failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private CallToolResult ExportStep(string path, IReadOnlyList<PartInstance> instances)
    {
        var solids = new List<(string Name, BrepSolid Solid)>();
        var skipped = new JsonArray();
        foreach (var part in instances.Select(i => i.Part).Distinct())
        {
            // The part's shared cached solid, like the viewer's own STEP export: no
            // fresh lowering per export.
            if (part.TryGetSolid() is { } solid)
                solids.Add((part.Name, solid));
            else
                skipped.Add(part.Name);
        }
        if (solids.Count == 0)
            return Error("No B-Rep-representable parts; STEP needs exact solids. Try .stl or .obj.");

        var written = new JsonArray();
        if (solids.Count == 1)
        {
            StepWriter.WriteFile(solids[0].Solid, path, solids[0].Name);
            written.Add(Path.GetFullPath(path));
        }
        else
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string stem = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            foreach (var (name, solid) in solids)
            {
                string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
                string partPath = Path.Combine(directory, $"{stem}.{safe}{extension}");
                StepWriter.WriteFile(solid, partPath, name);
                written.Add(partPath);
            }
        }
        var result = new JsonObject { ["wrote"] = written, ["format"] = "STEP AP214" };
        if (skipped.Count > 0)
            result["skipped"] = skipped;
        return Ok(result);
    }

    /// <summary>All instances merged into one OBJ with their world transforms applied
    /// (the same output <c>--export .obj</c> produces).</summary>
    private static void WriteMergedObj(
        IReadOnlyList<PartInstance> instances, string path, MeshQuality quality)
    {
        var culture = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path);
        int offset = 1;   // OBJ is 1-based
        foreach (var instance in instances)
        {
            writer.WriteLine($"o {instance.Path.Replace(' ', '_')}");
            var (positions, faces) = instance.Part.GetMesh(quality).ToIndexed();
            foreach (var position in positions)
            {
                var p = instance.World.TransformPoint(position);
                writer.WriteLine(string.Create(culture, $"v {p.X:R} {p.Y:R} {p.Z:R}"));
            }
            foreach (var face in faces)
            {
                writer.Write('f');
                foreach (int v in face)
                {
                    writer.Write(' ');
                    writer.Write((v + offset).ToString(culture));
                }
                writer.WriteLine();
            }
            offset += positions.Length;
        }
    }

    // ---- JSON helpers ----

    private static JsonObject ConstructionJson(ConstructionNode node)
    {
        var json = new JsonObject
        {
            ["label"] = node.Label,
            ["kind"] = node.Kind.ToString().ToLowerInvariant(),
            ["path"] = node.Path,
        };
        if (node.Detail is { } detail)
            json["detail"] = detail;
        if (node.Suppressed)
            json["suppressed"] = true;
        if (node.Children.Count > 0)
            json["children"] = new JsonArray([.. node.Children.Select(c => (JsonNode)ConstructionJson(c))]);
        return json;
    }

    private static JsonObject BoundsJson(in Aabb bounds) => bounds.IsEmpty
        ? new JsonObject { ["empty"] = true }
        : new JsonObject
        {
            ["min"] = PointJson(bounds.Min),
            ["max"] = PointJson(bounds.Max),
            ["size"] = PointJson(bounds.Size),
        };

    private static JsonArray PointJson(in Vector3d p) => new(p.X, p.Y, p.Z);

    private static string KindOf(Part part) => part.Geometry switch
    {
        Shape => "Shape (unified)",
        BrepSolid => "B-Rep solid",
        HalfEdgeMesh => "mesh",
        Sdf => "implicit (SDF)",
        _ => part.Geometry.GetType().Name,
    };

    /// <summary>Whether the part has an exact B-Rep route (STEP-exportable). Cheap:
    /// <c>CanConvertTo</c> walks the operation graph without lowering it.</summary>
    private static bool IsBrepRepresentable(Part part)
    {
        try
        {
            return part.Geometry switch
            {
                BrepSolid => true,
                Shape shape => shape.CanConvertTo(TargetRep.Brep),
                _ => false,
            };
        }
        catch
        {
            return false;   // a graph that cannot even be explained is certainly not exact
        }
    }

    private string UnknownPart(string name, string? tab)
    {
        var names = Session.Scene.Tabs
            .SelectMany(t => t.AllParts.Select(p => $"{t.Name}/{p.Name}"))
            .Take(40);
        return tab is null
            ? $"No part named '{name}'. Parts in this scene: {string.Join(", ", names)}."
            : $"No part named '{name}' in tab '{tab}'. Parts in this scene: {string.Join(", ", names)}.";
    }

    private string UnknownTab(string tab) =>
        $"No tab named '{tab}'. Tabs in this scene: "
        + $"{string.Join(", ", Session.Scene.Tabs.Select(t => t.Name))}.";

    private static CallToolResult Ok(JsonObject payload) => new()
    {
        Content = [new TextContentBlock { Text = payload.ToJsonString(Json) }],
    };

    private static CallToolResult Error(string? message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message ?? "unknown error" }],
    };
}
