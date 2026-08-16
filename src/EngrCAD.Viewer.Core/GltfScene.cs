using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// Turning a document into a glTF node forest. This lives in Viewer.Core rather than in
// Modeling for one reason: a glTF file is "what you see" written down, and everything
// that decides what you see already lives here -- the colour-map tables beside the
// shaders, FieldRendering's per-source colour rule, DisplayMode's translucency. Putting
// the bridge in Modeling would need either a second copy of those or a Modeling->Viewer
// reference; putting it here costs nothing, since every consumer of --export and the MCP
// export tool already references the viewer.
//
// The structural decision is the same one StepWriter.WriteAssembly makes: ONE glTF mesh
// per distinct Part, referenced by one node per placement, so a fastener placed fifty
// times is written once. That is the property the baking exporters (STL, 3MF, OBJ)
// structurally cannot have, and it is most of why glTF is worth having.

/// <summary>
/// Builds a <see cref="GltfWriter"/> node forest from an EngrCAD <see cref="Scene"/>,
/// <see cref="Tab"/> or <see cref="Assembly"/> — preserving the assembly hierarchy rather
/// than flattening it, sharing one mesh per distinct <see cref="Part"/>, and carrying
/// per-part colours, translucency and simulation-result colours across.
/// </summary>
public static class GltfScene
{
    /// <summary>What a document lowered to for glTF: the distinct geometries and the node
    /// forest that places them, plus the parts that were skipped and why.</summary>
    /// <param name="Geometries">One entry per distinct meshed <see cref="Part"/>.</param>
    /// <param name="Roots">The exported node forest (one root per tab).</param>
    /// <param name="Skipped">Parts that could not be meshed, with the reason.</param>
    public readonly record struct GltfPlan(
        IReadOnlyList<GltfGeometry> Geometries,
        IReadOnlyList<GltfNode> Roots,
        IReadOnlyList<(Part Part, string Reason)> Skipped);

    /// <summary>
    /// Plans a whole scene: one root node per tab, each carrying that tab's loose parts
    /// and assembly trees.
    /// </summary>
    /// <param name="scene">The document.</param>
    /// <param name="quality">Meshing quality; null resolves the scene's own.</param>
    /// <param name="explode">Explode factor, composed into occurrence frames exactly the
    /// way <see cref="Assembly.Flatten(double)"/> does — so an exported exploded view and
    /// a rendered one agree, and a factor of 0 leaves every frame untouched.</param>
    /// <param name="parts">The parts to include, or null for every visible part. Callers
    /// pass a debug-filtered list so ghosted and hidden parts never reach a file.</param>
    public static GltfPlan Plan(
        Scene scene, MeshQuality? quality = null, double explode = 0,
        IReadOnlyCollection<Part>? parts = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var builder = new Builder(scene.ResolveQuality(quality), parts);
        var roots = new List<GltfNode>();
        foreach (var tab in scene.Tabs)
        {
            var children = builder.TabChildren(tab, explode);
            if (children.Count != 0)
                roots.Add(new GltfNode(tab.Name) { Children = children });
        }
        return builder.Finish(roots);
    }

    /// <summary>Plans one tab.</summary>
    public static GltfPlan Plan(
        Tab tab, MeshQuality? quality = null, double explode = 0,
        IReadOnlyCollection<Part>? parts = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var builder = new Builder(quality, parts);
        return builder.Finish(builder.TabChildren(tab, explode));
    }

    /// <summary>
    /// Plans a FLAT list of already-posed instances: one node per instance, named by its
    /// occurrence path, with geometry still deduped by <see cref="Part"/> reference.
    /// <para>The hierarchy is gone (the caller already flattened it) but the instancing
    /// is not — a part placed fifty times is still one mesh. This is the seam for callers
    /// that hold a filtered or scoped instance list rather than a document, and it is the
    /// direct analogue of what the 3MF and AMF exporters take.</para>
    /// </summary>
    public static GltfPlan Plan(
        IReadOnlyList<PartInstance> instances, MeshQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var builder = new Builder(quality, null);
        var roots = new List<GltfNode>();
        foreach (var instance in instances)
        {
            if (builder.InstanceNode(instance) is { } node)
                roots.Add(node);
        }
        return builder.Finish(roots);
    }

    /// <summary>Plans one assembly, hierarchy preserved.</summary>
    public static GltfPlan Plan(
        Assembly assembly, MeshQuality? quality = null, double explode = 0)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var builder = new Builder(quality, null);
        var node = builder.AssemblyNode(assembly, assembly.Name, explode);
        return builder.Finish(node is null ? [] : [node]);
    }

    /// <summary>Plans and writes a scene in one call (container picked by extension).</summary>
    public static GltfPlan WriteFile(
        Scene scene, string path, MeshQuality? quality = null, double explode = 0,
        GltfOptions? options = null, IReadOnlyCollection<Part>? parts = null)
    {
        var plan = Plan(scene, quality, explode, parts);
        GltfWriter.WriteFile(
            plan.Geometries, plan.Roots, path,
            options ?? GltfOptions.Default with { SceneName = SceneName(scene) });
        return plan;
    }

    private static string SceneName(Scene scene) =>
        scene.Tabs.Count == 1 ? scene.Tabs[0].Name : "EngrCAD scene";

    private sealed class Builder(MeshQuality? quality, IReadOnlyCollection<Part>? parts)
    {
        private readonly List<GltfGeometry> _geometries = [];
        private readonly List<(Part Part, string Reason)> _skipped = [];
        // Keyed by REFERENCE, exactly as Bom and Scene.AllParts dedupe: two separately
        // built parts that happen to be identical stay two meshes, and one part placed
        // many times stays one.
        private readonly Dictionary<Part, int> _index = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Part>? _allowed = parts is null
            ? null
            : new HashSet<Part>(parts, ReferenceEqualityComparer.Instance);

        public GltfPlan Finish(IReadOnlyList<GltfNode> roots) => new(_geometries, roots, _skipped);

        public List<GltfNode> TabChildren(Tab tab, double explode)
        {
            var children = new List<GltfNode>();
            foreach (var part in tab.Parts)
            {
                if (PartNode(part, part.Name, part.Transform) is { } node)
                    children.Add(node);
            }
            foreach (var assembly in tab.Assemblies)
            {
                if (AssemblyNode(assembly, assembly.Name, explode) is { } node)
                    children.Add(node);
            }
            return children;
        }

        public GltfNode? InstanceNode(in PartInstance instance) =>
            PartNode(instance.Part, instance.Path, instance.World);

        public GltfNode? AssemblyNode(Assembly assembly, string name, double explode)
        {
            var children = new List<GltfNode>();
            foreach (var occurrence in assembly.Occurrences)
            {
                var frame = Posed(occurrence, explode);
                if (occurrence.Part is { } part)
                {
                    // The occurrence's own pose times the part's transform: exactly what
                    // Assembly.FlattenInto composes into PartInstance.World, kept as a
                    // node matrix here instead of baked into vertices.
                    if (PartNode(part, occurrence.Name, frame.ToMatrix() * part.Transform) is { } node)
                        children.Add(node);
                }
                else if (AssemblyNode(occurrence.SubAssembly!, occurrence.Name, explode) is { } node)
                {
                    // A sub-assembly's own node carries its occurrence frame; its children
                    // are then relative to it, which is the whole point of keeping the
                    // hierarchy.
                    children.Add(node with { Transform = frame.ToMatrix() });
                }
            }
            return children.Count == 0 ? null : new GltfNode(name) { Children = children };
        }

        /// <summary>An occurrence's pose with the explode displacement applied — the same
        /// rule <c>Assembly.Flatten</c> uses, and exactly the original frame at factor 0
        /// so an un-exploded export is bit-for-bit the assembled one.</summary>
        private static Frame3d Posed(Occurrence occurrence, double explode)
        {
            if (occurrence.ExplodeOffset is not { } offset || explode == 0)
                return occurrence.Frame;
            var frame = occurrence.Frame;
            return Frame3d.FromOrthonormal(frame.Origin + offset * explode, frame.X, frame.Y);
        }

        private GltfNode? PartNode(Part part, string name, in Matrix4d transform)
        {
            if (_allowed is not null && !_allowed.Contains(part))
                return null;
            int? geometry = GeometryFor(part);
            return geometry is null
                ? null
                : new GltfNode(name) { Transform = transform, Geometry = geometry };
        }

        private int? GeometryFor(Part part)
        {
            if (_index.TryGetValue(part, out int existing))
                return existing;
            if (_skipped.Any(s => ReferenceEquals(s.Part, part)))
                return null;

            HalfEdgeMesh mesh;
            try
            {
                mesh = part.GetMesh(quality);
            }
            catch (Exception ex)
            {
                // A part that will not mesh is NAMED and dropped, never swallowed — the
                // TabMeshLoader contract, applied to export.
                _skipped.Add((part, ex.Message));
                return null;
            }

            var geometry = new GltfGeometry(mesh, part.Name)
            {
                Color = part.Color is { } c ? (c.R, c.G, c.B) : null,
                Opacity = part.EffectiveDisplayMode == DisplayMode.Translucent
                    ? TranslucentAlpha
                    : 1f,
                VertexColors = FieldColors(part, mesh),
            };
            _index[part] = _geometries.Count;
            _geometries.Add(geometry);
            return _geometries.Count - 1;
        }

        /// <summary>
        /// A part's result colours, or null when it shows no field.
        /// <para>The COLOURS travel; the DEFORMATION deliberately does not. An exaggeration
        /// factor is a viewing parameter and glTF has nowhere to record one, so a file
        /// carrying 50x-displaced geometry would be indistinguishable from a model that
        /// really is that shape — which is a worse failure than not exporting it.</para>
        /// <para>A display that cannot be honoured (a result an edit removed) is skipped
        /// silently here: the part still exports, uncoloured, and the viewer and the MCP
        /// tools already report the same failure as a status message.</para>
        /// </summary>
        private static IReadOnlyList<(float R, float G, float B)>? FieldColors(
            Part part, HalfEdgeMesh mesh)
        {
            if (!part.TryResolveFieldDisplay(out var display, out _))
                return null;
            if (display.Field.Count != mesh.VertexCount)
                return null;
            return FieldRendering.SourceColors(
                display.Field, display.Range, display.ColorMap, display.LogScale);
        }
    }

    /// <summary>Alpha a <see cref="DisplayMode.Translucent"/> part exports at — the value
    /// the render passes blend it with, so a see-through part stays see-through.</summary>
    public const float TranslucentAlpha = 0.4f;
}
