using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>RGB color in [0, 1] for display purposes (UI-framework free).</summary>
public readonly record struct PartColor(float R, float G, float B);

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

/// <summary>Tessellation quality applied when geometry is added to a scene.</summary>
public sealed class SceneOptions
{
    public int SegmentsPerCircle { get; set; } = 32;
    public int CurveSamples { get; set; } = 24;
    public int SdfResolution { get; set; } = 64;
}

/// <summary>A named displayable body: tessellated mesh + color + placement.</summary>
public sealed class Part
{
    public string Name { get; }
    public HalfEdgeMesh Mesh { get; }
    public PartColor Color { get; }
    public Matrix4d Transform { get; }

    /// <summary>The geometry the part was created from (BrepSolid, HalfEdgeMesh, or Sdf).</summary>
    public object Source { get; }

    internal Part(string name, HalfEdgeMesh mesh, PartColor color, Matrix4d transform, object source)
    {
        Name = name;
        Mesh = mesh;
        Color = color;
        Transform = transform;
        Source = source;
    }
}

/// <summary>
/// The document model design code builds and the viewer displays: an ordered set of
/// named parts. Geometry from any engine is accepted and tessellated on add
/// (B-Rep via <see cref="BRepTessellator"/>, implicit via <see cref="SurfaceNets"/>,
/// meshes as-is). UI-free — safe to construct in scripts, tests, and exporters.
/// </summary>
public sealed class Scene
{
    private readonly List<Part> _parts = [];
    private int _nextColor;

    public SceneOptions Options { get; }

    public Scene(SceneOptions? options = null) => Options = options ?? new SceneOptions();

    public IReadOnlyList<Part> Parts => _parts;

    public Part Add(string name, BrepSolid solid, PartColor? color = null, Matrix4d? transform = null) =>
        AddPart(name, BRepTessellator.Tessellate(solid, Options.SegmentsPerCircle, Options.CurveSamples),
            color, transform, solid);

    public Part Add(string name, HalfEdgeMesh mesh, PartColor? color = null, Matrix4d? transform = null) =>
        AddPart(name, mesh, color, transform, mesh);

    /// <summary>Adds an implicit body, meshed over its own bounds (must be finite).</summary>
    public Part Add(string name, Sdf sdf, PartColor? color = null, Matrix4d? transform = null) =>
        AddPart(name, SurfaceNets.Polygonize(sdf, Options.SdfResolution), color, transform, sdf);

    internal Part AddPart(string name, HalfEdgeMesh mesh, PartColor? color, Matrix4d? transform, object source)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Part name must be non-empty.", nameof(name));
        if (_parts.Any(p => p.Name == name))
            throw new ArgumentException($"The scene already contains a part named '{name}'.", nameof(name));

        var part = new Part(
            name, mesh,
            color ?? Palette.Cycle[_nextColor++ % Palette.Cycle.Length],
            transform ?? Matrix4d.Identity,
            source);
        _parts.Add(part);
        return part;
    }

    /// <summary>World-space bounds of all parts (transforms applied); used for camera framing.</summary>
    public Aabb Bounds()
    {
        var bounds = Aabb.Empty;
        foreach (var part in _parts)
        {
            var local = part.Mesh.ComputeBounds();
            if (local.IsEmpty)
                continue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3d(
                    (i & 1) == 0 ? local.Min.X : local.Max.X,
                    (i & 2) == 0 ? local.Min.Y : local.Max.Y,
                    (i & 4) == 0 ? local.Min.Z : local.Max.Z);
                bounds = bounds.Union(part.Transform.TransformPoint(corner));
            }
        }
        return bounds;
    }
}
