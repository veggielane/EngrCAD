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

    /// <summary>Display color; when null, the tab assigns the next palette color on add.</summary>
    public PartColor? Color { get; set; }

    /// <summary>How viewers draw this part (shaded, wireframe, or translucent).
    /// Viewers may also change it interactively (per-part cycler in the model tree).</summary>
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Shaded;

    public Matrix4d Transform { get; set; } = Matrix4d.Identity;

    private readonly Lock _meshLock = new();
    private HalfEdgeMesh? _mesh;

    public Part(string name, Shape shape, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)shape, color, transform) { }

    public Part(string name, BrepSolid solid, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)solid, color, transform) { }

    public Part(string name, HalfEdgeMesh mesh, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)mesh, color, transform) { }

    public Part(string name, Sdf sdf, PartColor? color = null, Matrix4d? transform = null)
        : this(name, (object)sdf, color, transform) { }

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

    /// <summary>World-space bounds of the display mesh with <see cref="Transform"/> applied.</summary>
    public Aabb Bounds(MeshQuality? quality = null)
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
            bounds = bounds.Union(Transform.TransformPoint(corner));
        }
        return bounds;
    }
}

/// <summary>
/// A named group of parts shown together — one viewer tab. (Future: tabs will also
/// hold assembly occurrences — placed instances of parts/sub-assemblies — so keep
/// <see cref="Part"/> a leaf and this the container.)
/// </summary>
public sealed class Tab
{
    private readonly List<Part> _parts = [];
    private int _nextColor;

    public string Name { get; }

    internal Tab(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tab name must be non-empty.", nameof(name));
        Name = name;
    }

    public IReadOnlyList<Part> Parts => _parts;

    /// <summary>Adds a part (names must be unique within the tab); assigns the next
    /// palette color when the part has none. Returns the part for chaining.</summary>
    public Part Add(Part part)
    {
        if (_parts.Any(p => p.Name == part.Name))
            throw new ArgumentException($"Tab '{Name}' already contains a part named '{part.Name}'.", nameof(part));
        part.Color ??= Palette.Cycle[_nextColor++ % Palette.Cycle.Length];
        _parts.Add(part);
        return part;
    }

    /// <summary>World-space bounds of all parts; used for camera framing.</summary>
    public Aabb Bounds(MeshQuality? quality = null)
    {
        var bounds = Aabb.Empty;
        foreach (var part in _parts)
            bounds = bounds.Union(part.Bounds(quality));
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

    public Scene(MeshQuality? options = null) => Options = options ?? new MeshQuality();

    public IReadOnlyList<Tab> Tabs => _tabs;

    public IEnumerable<Part> AllParts => _tabs.SelectMany(t => t.Parts);

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

    /// <summary>Produces every part's display mesh at this scene's quality (idempotent).
    /// Hosts call this off the UI thread before handing tabs to the viewport.</summary>
    public void PreMesh()
    {
        foreach (var part in AllParts)
            part.GetMesh(Options);
    }
}
