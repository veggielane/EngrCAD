using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// A flattened, renderable instance of a part: the shared <see cref="Part"/> (whose
/// display mesh is produced once per part and cached), the composed world transform
/// (occurrence frames down the assembly tree, then the part's own
/// <see cref="Modeling.Part.Transform"/>), and the occurrence path
/// ("gearbox/stack.2/bolt"; a loose tab part's path is just its name). This is the
/// seam viewers and exporters consume — they never walk assemblies themselves.
/// </summary>
public readonly record struct PartInstance(Part Part, Matrix4d World, string Path)
{
    /// <summary>World-space bounds of this instance's display mesh.</summary>
    public Aabb Bounds(MeshQuality? quality = null) => Part.Bounds(World, quality);
}

/// <summary>
/// One placed item inside an <see cref="Assembly"/>: a reference to a shared
/// <see cref="Part"/> OR a nested <see cref="Assembly"/>, plus a rigid pose
/// (<see cref="Frame"/>) relative to the parent assembly. Names are unique per
/// assembly level; derived names auto-suffix (".2", ".3", …) like CAD occurrence
/// lists, explicit duplicate names throw.
/// </summary>
public sealed class Occurrence
{
    public string Name { get; }

    /// <summary>The referenced part (null when this occurrence places a sub-assembly).</summary>
    public Part? Part { get; }

    /// <summary>The nested assembly (null when this occurrence places a part).</summary>
    public Assembly? SubAssembly { get; }

    /// <summary>Pose relative to the parent assembly; poses compose down the tree
    /// (<c>child.Frame.Then(parentWorld)</c>). Mutable so parametric design code can
    /// re-pose occurrences between live reloads.</summary>
    public Frame3d Frame { get; set; }

    /// <summary>True when this occurrence places a nested assembly.</summary>
    public bool IsAssembly => SubAssembly is not null;

    internal Occurrence(string name, Part? part, Assembly? subAssembly, in Frame3d frame)
    {
        Name = name;
        Part = part;
        SubAssembly = subAssembly;
        Frame = frame;
    }
}

/// <summary>
/// A named collection of <see cref="Occurrence"/>s — parts and sub-assemblies, each
/// with a rigid <see cref="Frame3d"/> pose. Assemblies hold references: the same
/// <see cref="Part"/> (or <see cref="Assembly"/>) placed several times is shared, so
/// its display mesh is produced once and every instance renders with its own composed
/// world transform. Added to a <see cref="Tab"/> next to loose parts;
/// <see cref="Flatten()"/> resolves the tree to posed <see cref="PartInstance"/>s.
/// v1 is occurrences only — mates/constraints, exploded views, and BOM are future work.
/// </summary>
public sealed class Assembly
{
    private readonly List<Occurrence> _occurrences = [];

    public string Name { get; }

    public Assembly(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Assembly name must be non-empty.", nameof(name));
        Name = name;
    }

    public IReadOnlyList<Occurrence> Occurrences => _occurrences;

    /// <summary>
    /// Places a part at <paramref name="frame"/> (identity when omitted). The
    /// occurrence name defaults to the part's name, auto-suffixed ".2", ".3", … for
    /// repeat placements; an explicit <paramref name="name"/> must be unique.
    /// </summary>
    public Occurrence Add(Part part, Frame3d? frame = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        var occurrence = new Occurrence(ResolveName(name, part.Name), part, null, frame ?? Frame3d.WorldXY);
        _occurrences.Add(occurrence);
        return occurrence;
    }

    /// <summary>
    /// Places a nested assembly at <paramref name="frame"/> (identity when omitted).
    /// Rejects placements that would make the assembly graph cyclic. Naming follows
    /// <see cref="Add(Part, Frame3d?, string?)"/>.
    /// </summary>
    public Occurrence Add(Assembly subAssembly, Frame3d? frame = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(subAssembly);
        if (ReferenceEquals(subAssembly, this) || subAssembly.Contains(this))
            throw new ArgumentException(
                $"Adding '{subAssembly.Name}' to '{Name}' would create an assembly cycle.", nameof(subAssembly));
        var occurrence = new Occurrence(
            ResolveName(name, subAssembly.Name), null, subAssembly, frame ?? Frame3d.WorldXY);
        _occurrences.Add(occurrence);
        return occurrence;
    }

    private string ResolveName(string? explicitName, string baseName)
    {
        if (explicitName is not null)
        {
            if (string.IsNullOrWhiteSpace(explicitName))
                throw new ArgumentException("Occurrence name must be non-empty.", nameof(explicitName));
            if (explicitName.Contains('/'))
                throw new ArgumentException(
                    "Occurrence names cannot contain '/' (it separates occurrence paths).", nameof(explicitName));
            if (_occurrences.Any(o => o.Name == explicitName))
                throw new ArgumentException(
                    $"Assembly '{Name}' already contains an occurrence named '{explicitName}'.", nameof(explicitName));
            return explicitName;
        }

        string candidate = baseName.Replace('/', '-');
        if (_occurrences.All(o => o.Name != candidate))
            return candidate;
        for (int k = 2; ; k++)
        {
            string suffixed = $"{candidate}.{k}";
            if (_occurrences.All(o => o.Name != suffixed))
                return suffixed;
        }
    }

    /// <summary>True when <paramref name="assembly"/> appears anywhere in this subtree.</summary>
    internal bool Contains(Assembly assembly)
    {
        foreach (var occurrence in _occurrences)
        {
            if (occurrence.SubAssembly is { } sub &&
                (ReferenceEquals(sub, assembly) || sub.Contains(assembly)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every distinct <see cref="Part"/> referenced in this subtree, each exactly once
    /// (reference identity) — the set to pre-mesh, however many times each is placed.
    /// </summary>
    public IReadOnlyList<Part> DistinctParts()
    {
        var seen = new HashSet<Part>();
        var parts = new List<Part>();
        Collect(seen, parts);
        return parts;
    }

    private void Collect(HashSet<Part> seen, List<Part> into)
    {
        foreach (var occurrence in _occurrences)
        {
            if (occurrence.Part is { } part)
            {
                if (seen.Add(part))
                    into.Add(part);
            }
            else
            {
                occurrence.SubAssembly!.Collect(seen, into);
            }
        }
    }

    /// <summary>Flattens the tree to posed part instances (paths rooted at this
    /// assembly's name), composing occurrence frames depth-first.</summary>
    public IReadOnlyList<PartInstance> Flatten() => Flatten(Frame3d.WorldXY);

    /// <summary>Flattens with the whole assembly posed by <paramref name="root"/>.</summary>
    public IReadOnlyList<PartInstance> Flatten(in Frame3d root)
    {
        var instances = new List<PartInstance>();
        FlattenInto(root, Name, instances);
        return instances;
    }

    internal void FlattenInto(in Frame3d parentWorld, string parentPath, List<PartInstance> into)
    {
        foreach (var occurrence in _occurrences)
        {
            var world = occurrence.Frame.Then(parentWorld);
            string path = $"{parentPath}/{occurrence.Name}";
            if (occurrence.Part is { } part)
                into.Add(new PartInstance(part, world.ToMatrix() * part.Transform, path));
            else
                occurrence.SubAssembly!.FlattenInto(world, path, into);
        }
    }

    /// <summary>World-space bounds of the flattened instances.</summary>
    public Aabb Bounds(MeshQuality? quality = null)
    {
        var bounds = Aabb.Empty;
        foreach (var instance in Flatten())
            bounds = bounds.Union(instance.Bounds(quality));
        return bounds;
    }
}
