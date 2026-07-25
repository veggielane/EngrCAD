using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>RGB color in [0, 1] for display purposes (UI-framework free).</summary>
public readonly record struct PartColor(float R, float G, float B);

/// <summary>How a part is drawn in a viewer (display metadata, UI-framework free).</summary>
public enum DisplayMode
{
    /// <summary>Lit solid fill with feature-edge overlay (the default).</summary>
    Shaded,

    /// <summary>Triangle edges only, no fill — see through to what is behind.</summary>
    Wireframe,

    /// <summary>See-through fill (alpha-blended) with feature edges, for revealing interiors.</summary>
    Translucent,
}

/// <summary>A pleasant default palette; parts added without a color cycle through it.</summary>
public static class Palette
{
    public static readonly PartColor Steel = new(0.55f, 0.68f, 0.84f);
    public static readonly PartColor Brass = new(0.85f, 0.72f, 0.38f);
    public static readonly PartColor Copper = new(0.86f, 0.60f, 0.50f);
    public static readonly PartColor Sage = new(0.63f, 0.78f, 0.58f);
    public static readonly PartColor Coral = new(0.88f, 0.52f, 0.40f);
    public static readonly PartColor Sky = new(0.42f, 0.62f, 0.86f);
    public static readonly PartColor Plum = new(0.72f, 0.55f, 0.83f);
    public static readonly PartColor Teal = new(0.47f, 0.79f, 0.77f);
    public static readonly PartColor Rose = new(0.83f, 0.47f, 0.62f);
    public static readonly PartColor Slate = new(0.62f, 0.66f, 0.88f);

    internal static readonly PartColor[] Cycle =
        [Steel, Coral, Sage, Plum, Brass, Teal, Rose, Sky, Copper, Slate];
}

/// <summary>
/// A named, displayable body: geometry from any engine (<see cref="Shape"/>,
/// <see cref="BrepSolid"/>, <see cref="HalfEdgeMesh"/>, or <see cref="Sdf"/>) plus
/// color and placement. Parts carry all their own information and are added to a
/// scene's tabs; the display mesh is produced on first use and cached.
/// </summary>
public sealed class Part
{
    public string Name { get; }

    /// <summary>The geometry the part was created from (Shape, BrepSolid, HalfEdgeMesh, or Sdf).</summary>
    public object Geometry { get; }

    /// <summary>The parametric history this part was regenerated from, when it came
    /// from one (<see cref="FeatureHistory.ToPart"/>); null for directly built parts.
    /// Its presence makes <see cref="ConstructionTree"/> show features instead of the
    /// resulting shape graph.</summary>
    public FeatureHistory? History { get; }

    /// <summary>Display color; when null, the tab assigns the next palette color on add.</summary>
    public PartColor? Color { get; set; }

    /// <summary>How viewers draw this part (shaded, wireframe, or translucent).
    /// Viewers may also change it interactively (per-part cycler in the model tree).</summary>
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Shaded;

    public Matrix4d Transform { get; set; } = Matrix4d.Identity;

    private readonly Lock _meshLock = new();
    private HalfEdgeMesh? _mesh;

    // ---- annotations (PMI) ----
    private readonly List<Annotation> _annotationList = [];
    private (IReadOnlyList<ResolvedAnnotation>? Resolved, string? Error)? _resolvedAnnotations;
    private BrepSolid? _annotationSolid;   // lazily lowered target for selector queries

    public Part(string name, Shape shape, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)shape, color, transform) { }

    public Part(string name, BrepSolid solid, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)solid, color, transform) { }

    public Part(string name, HalfEdgeMesh mesh, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)mesh, color, transform) { }

    public Part(string name, Sdf sdf, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)sdf, color, transform) { }

    /// <summary>
    /// A part from a parametric history: regenerates it now and keeps the history, so
    /// viewers can show the ordered feature list (see <see cref="ConstructionTree"/>)
    /// instead of the resulting shape graph.
    /// </summary>
    public Part(string name, FeatureHistory history, PartColor? color = null, Matrix4d? transform = null)
        : this(name, RegeneratedBody(history), history, color, transform) { }

    /// <summary>Already-regenerated body plus its history (<see cref="FeatureHistory.ToPart"/>).</summary>
    internal Part(string name, Shape body, FeatureHistory history, PartColor? color, Matrix4d? transform)
        : this(name, (object)body, color, transform) => History = history;

    private static Shape RegeneratedBody(FeatureHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var result = history.Regenerate();
        return result.Body
            ?? throw new InvalidOperationException($"The history produced no body:\n{result}");
    }

    private Part(string name, object geometry, PartColor? color, Matrix4d? transform)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Part name must be non-empty.", nameof(name));
        Name = name;
        Geometry = geometry;
        Color = color;
        if (transform is { } t)
            Transform = t;
    }

    /// <summary>
    /// The display mesh, produced on first call (Shapes via their best route, B-Reps
    /// via tessellation, SDFs via Surface Nets, meshes as-is) and cached — the first
    /// caller's <paramref name="quality"/> wins. Scenes pre-mesh all parts with their
    /// own quality, so parts shown through a scene use the scene's settings.
    /// </summary>
    public HalfEdgeMesh GetMesh(MeshQuality? quality = null)
    {
        lock (_meshLock)
        {
            return _mesh ??= Geometry switch
            {
                HalfEdgeMesh mesh => mesh,
                Shape shape => shape.ToMesh(quality),
                BrepSolid solid => BRepTessellator.Tessellate(
                    solid,
                    (quality ?? MeshQuality.Default).SegmentsPerCircle,
                    (quality ?? MeshQuality.Default).CurveSamples),
                Sdf sdf => SurfaceNets.Polygonize(sdf, (quality ?? MeshQuality.Default).SdfResolution),
                _ => throw new InvalidOperationException($"Unknown geometry type {Geometry.GetType().Name}."),
            };
        }
    }

    private ConstructionNode? _constructionTree;
    private bool _constructionTreeBuilt;

    /// <summary>
    /// How this part was built, as a row tree a viewer can expand: the ordered feature
    /// list when the part came from a <see cref="FeatureHistory"/>, otherwise the
    /// <see cref="Shape"/> operation graph, otherwise null (raw B-Rep/mesh/SDF parts
    /// carry no construction information). Built once and cached — a part's geometry is
    /// fixed at construction, so node references are stable and usable as preview-cache
    /// keys (<see cref="ConstructionPreviewCache"/>).
    /// </summary>
    public ConstructionNode? ConstructionTree()
    {
        lock (_meshLock)
        {
            if (!_constructionTreeBuilt)
            {
                _constructionTreeBuilt = true;
                _constructionTree = Modeling.ConstructionTree.For(this);
            }
            return _constructionTree;
        }
    }

    /// <summary>The 3D annotations (PMI) attached to this part — dimensions, notes,
    /// datum labels — in part-local coordinates (posed with the part).</summary>
    public IReadOnlyList<Annotation> Annotations
    {
        get
        {
            lock (_meshLock)
                return [.. _annotationList];
        }
    }

    /// <summary>Attaches an annotation (chainable). Selector-based dimensions
    /// re-measure against this part's geometry when resolved.</summary>
    public Part Annotate(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        lock (_meshLock)
        {
            _annotationList.Add(annotation);
            _resolvedAnnotations = null;   // resolved cache is stale
        }
        return this;
    }

    /// <summary>
    /// Resolves all attached annotations against this part's geometry (selector-based
    /// dimensions lower the geometry to B-Rep once, cached) and returns the
    /// render-ready list, in part-local coordinates. Cached until an annotation is
    /// added. Throws when any annotation fails to resolve; viewers use
    /// <see cref="TryResolveAnnotations"/> for a non-throwing path.
    /// </summary>
    public IReadOnlyList<ResolvedAnnotation> ResolveAnnotations() =>
        TryResolveAnnotations(out var resolved, out string? error)
            ? resolved
            : throw new InvalidOperationException(error);

    /// <summary>
    /// Non-throwing <see cref="ResolveAnnotations"/>: true with the resolved list on
    /// success (empty when the part has no annotations); false with a diagnostic when
    /// any annotation fails (bad selector after an edit, non-B-Rep geometry under a
    /// selector dimension). The result — success or failure — is cached, so viewers
    /// can call this per frame; <c>Scene.PreMesh</c> pre-resolves off the render
    /// thread the same way it pre-meshes.
    /// </summary>
    public bool TryResolveAnnotations(
        out IReadOnlyList<ResolvedAnnotation> resolved, out string? error)
    {
        lock (_meshLock)
        {
            if (_resolvedAnnotations is { } cached)
            {
                resolved = cached.Resolved ?? [];
                error = cached.Error;
                return error is null;
            }

            var list = new List<ResolvedAnnotation>(_annotationList.Count);
            foreach (var annotation in _annotationList)
            {
                try
                {
                    list.Add(annotation.Resolve(LowerForAnnotations));
                }
                catch (Exception e)
                {
                    error = $"Part '{Name}': {annotation.GetType().Name} failed to resolve: {e.Message}";
                    _resolvedAnnotations = (null, error);
                    resolved = [];
                    return false;
                }
            }
            _resolvedAnnotations = (list, null);
            resolved = list;
            error = null;
            return true;
        }
    }

    /// <summary>The selector target: this part's geometry as a B-Rep (lowered once,
    /// cached — Part geometry is fixed at construction).</summary>
    private BrepSolid LowerForAnnotations() => _annotationSolid ??= Geometry switch
    {
        BrepSolid solid => solid,
        Shape shape => shape.ToBrep(),
        _ => throw new InvalidOperationException(
            $"selector-based annotations need B-Rep-representable geometry " +
            $"(this part holds {Geometry.GetType().Name})."),
    };

    private IReadOnlyList<(Vector3d A, Vector3d B)>? _featureEdges;

    /// <summary>
    /// Feature-edge segments for display overlays, produced on first call and cached
    /// (like <see cref="GetMesh"/>, the first caller's quality wins; scenes prime it
    /// in <see cref="Scene.PreMesh"/> so no B-Rep lowering happens on a render
    /// thread). Parts with B-Rep geometry — a <see cref="BrepSolid"/>, or a
    /// <see cref="Shape"/> with a B-Rep lowering — take their edges from the ACTUAL
    /// B-Rep edges (<c>BrepFeatureEdges</c> in EngrCAD.Interop), sampled at display
    /// resolution (at least 96 segments per circle regardless of the mesh quality):
    /// exact circles stay smooth however coarse the tessellation. Everything else
    /// (SDF and mesh parts, or a failed lowering) falls back to mesh-dihedral
    /// extraction over the display mesh, the previous behavior.
    /// </summary>
    public IReadOnlyList<(Vector3d A, Vector3d B)> GetFeatureEdges(MeshQuality? quality = null)
    {
        lock (_meshLock)
        {
            if (_featureEdges is not null)
                return _featureEdges;
            var q = quality ?? MeshQuality.Default;
            var solid = Geometry switch
            {
                BrepSolid direct => direct,
                Shape shape => TryLowerBrep(shape),
                _ => null,
            };
            if (solid is not null)
            {
                try
                {
                    return _featureEdges = BrepFeatureEdges.Extract(
                        solid,
                        Math.Max(96, q.SegmentsPerCircle),
                        Math.Max(48, q.CurveSamples));
                }
                catch
                {
                    // Any extraction hiccup falls back to the mesh route below.
                }
            }
            return _featureEdges = MeshFeatureEdges.Extract(GetMesh(quality));
        }

        static BrepSolid? TryLowerBrep(Shape shape)
        {
            try
            {
                return shape.CanConvertTo(TargetRep.Brep) ? shape.ToBrep() : null;
            }
            catch
            {
                return null;   // failed lowering: mesh-dihedral fallback
            }
        }
    }

    /// <summary>World-space bounds of the display mesh with <see cref="Transform"/> applied.</summary>
    public Aabb Bounds(MeshQuality? quality = null) => Bounds(Transform, quality);

    /// <summary>World-space bounds of the display mesh under an arbitrary placement —
    /// assembly instances pass their composed <see cref="PartInstance.World"/>.</summary>
    public Aabb Bounds(in Matrix4d world, MeshQuality? quality = null)
    {
        var local = GetMesh(quality).ComputeBounds();
        if (local.IsEmpty)
            return Aabb.Empty;
        var bounds = Aabb.Empty;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(
                (i & 1) == 0 ? local.Min.X : local.Max.X,
                (i & 2) == 0 ? local.Min.Y : local.Max.Y,
                (i & 4) == 0 ? local.Min.Z : local.Max.Z);
            bounds = bounds.Union(world.TransformPoint(corner));
        }
        return bounds;
    }
}

/// <summary>
/// A named group of content shown together — one viewer tab. Holds loose
/// <see cref="Part"/>s (posed by their own <see cref="Part.Transform"/>) and
/// <see cref="Assembly"/>s (posed occurrence trees); <see cref="Instances"/> flattens
/// both into the posed <see cref="PartInstance"/> list viewers render.
/// <see cref="Part"/> stays a leaf — this and <see cref="Assembly"/> are the containers.
/// </summary>
public sealed class Tab
{
    private readonly List<Part> _parts = [];
    private readonly List<Assembly> _assemblies = [];
    private int _nextColor;

    public string Name { get; }

    internal Tab(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tab name must be non-empty.", nameof(name));
        Name = name;
    }

    public IReadOnlyList<Part> Parts => _parts;

    public IReadOnlyList<Assembly> Assemblies => _assemblies;

    /// <summary>Adds a part (names must be unique within the tab, across parts and
    /// assemblies); assigns the next palette color when the part has none. Returns the
    /// part for chaining.</summary>
    public Part Add(Part part)
    {
        if (_parts.Any(p => p.Name == part.Name) || _assemblies.Any(a => a.Name == part.Name))
            throw new ArgumentException($"Tab '{Name}' already contains a part named '{part.Name}'.", nameof(part));
        part.Color ??= Palette.Cycle[_nextColor++ % Palette.Cycle.Length];
        _parts.Add(part);
        return part;
    }

    /// <summary>Adds an assembly (names must be unique within the tab, across parts and
    /// assemblies); assigns palette colors to its distinct parts that have none.
    /// Returns the assembly for chaining.</summary>
    public Assembly Add(Assembly assembly)
    {
        if (_parts.Any(p => p.Name == assembly.Name) || _assemblies.Any(a => a.Name == assembly.Name))
            throw new ArgumentException(
                $"Tab '{Name}' already contains an item named '{assembly.Name}'.", nameof(assembly));
        foreach (var part in assembly.DistinctParts())
            part.Color ??= Palette.Cycle[_nextColor++ % Palette.Cycle.Length];
        _assemblies.Add(assembly);
        return assembly;
    }

    /// <summary>
    /// The tab flattened for display: loose parts first (world = the part's own
    /// transform, path = its name), then each assembly's instances depth-first
    /// (paths like "gearbox/stack.2/bolt"). This ordered list is the seam viewers
    /// consume — instance index i here is instance index i in the viewport.
    /// </summary>
    public IReadOnlyList<PartInstance> Instances()
    {
        var instances = new List<PartInstance>(_parts.Count);
        foreach (var part in _parts)
            instances.Add(new PartInstance(part, part.Transform, part.Name));
        foreach (var assembly in _assemblies)
            assembly.FlattenInto(Frame3d.WorldXY, assembly.Name, instances);
        return instances;
    }

    /// <summary>Every distinct part shown in this tab — loose parts plus assembly
    /// parts, each exactly once (reference identity) however many times it is placed.</summary>
    public IEnumerable<Part> AllParts
    {
        get
        {
            var seen = new HashSet<Part>(_parts);
            foreach (var part in _parts)
                yield return part;
            foreach (var assembly in _assemblies)
            {
                foreach (var part in assembly.DistinctParts())
                {
                    if (seen.Add(part))
                        yield return part;
                }
            }
        }
    }

    /// <summary>World-space bounds of everything shown (loose parts and assembly
    /// instances); used for camera framing.</summary>
    public Aabb Bounds(MeshQuality? quality = null)
    {
        var bounds = Aabb.Empty;
        foreach (var instance in Instances())
            bounds = bounds.Union(instance.Bounds(quality));
        return bounds;
    }
}

/// <summary>
/// The document design code builds and the viewer displays: named tabs, each holding
/// parts. <see cref="Add"/> is a shorthand into a default "Model" tab for single-tab
/// designs. UI-free — safe in scripts, tests, and headless exporters.
/// </summary>
public sealed class Scene
{
    private readonly List<Tab> _tabs = [];

    public MeshQuality Options { get; }

    /// <summary>Whether <see cref="Options"/> was passed explicitly at construction.
    /// Hosts use this to decide quality precedence: a scene that chose its own quality
    /// always wins over host-level defaults (see <see cref="ResolveQuality"/>).</summary>
    public bool HasExplicitOptions { get; }

    public Scene(MeshQuality? options = null)
    {
        HasExplicitOptions = options is not null;
        Options = options ?? new MeshQuality();
    }

    public IReadOnlyList<Tab> Tabs => _tabs;

    /// <summary>Every distinct part in the scene — loose tab parts plus assembly parts,
    /// each exactly once (reference identity) however many times it is placed.</summary>
    public IEnumerable<Part> AllParts => _tabs.SelectMany(t => t.AllParts).Distinct();

    /// <summary>Every posed part instance across all tabs (see <see cref="Tab.Instances"/>).</summary>
    public IEnumerable<PartInstance> AllInstances => _tabs.SelectMany(t => t.Instances());

    public Tab AddTab(string name)
    {
        if (_tabs.Any(t => t.Name == name))
            throw new ArgumentException($"The scene already contains a tab named '{name}'.", nameof(name));
        var tab = new Tab(name);
        _tabs.Add(tab);
        return tab;
    }

    /// <summary>Adds a part to the default "Model" tab (created on first use).</summary>
    public Part Add(Part part)
    {
        var tab = _tabs.FirstOrDefault(t => t.Name == "Model") ?? AddTab("Model");
        return tab.Add(part);
    }

    /// <summary>
    /// Resolves the mesh quality this scene should be displayed/exported at, given an
    /// optional host-level <paramref name="fallback"/> (e.g. an <c>EngrCadOptions</c>
    /// quality). Precedence: options passed explicitly to the <see cref="Scene"/>
    /// constructor win; otherwise the host's fallback; otherwise this scene's default
    /// <see cref="MeshQuality"/>.
    /// </summary>
    public MeshQuality ResolveQuality(MeshQuality? fallback = null) =>
        HasExplicitOptions || fallback is null ? Options : fallback;

    /// <summary>Produces every distinct part's display mesh at this scene's quality
    /// (idempotent; a part instanced many times through assemblies is meshed once).
    /// Hosts call this off the UI thread before handing tabs to the viewport, and may
    /// pass their own default quality as <paramref name="fallback"/> — used only when
    /// the scene did not choose an explicit quality (see <see cref="ResolveQuality"/>).</summary>
    public void PreMesh(MeshQuality? fallback = null)
    {
        var quality = ResolveQuality(fallback);
        foreach (var part in AllParts)
        {
            part.GetMesh(quality);
            // Prime the feature-edge cache too: for Shape parts the B-Rep edge route
            // lowers the shape again, which must happen here (off the render thread),
            // not lazily inside a GL upload.
            part.GetFeatureEdges(quality);
            // Pre-resolve annotations too: selector dimensions lower to B-Rep, which
            // must not happen on the render thread. Failures are cached diagnostics
            // (viewers surface them via TryResolveAnnotations), not exceptions here.
            if (part.Annotations.Count > 0)
                part.TryResolveAnnotations(out _, out _);
        }
    }
}
