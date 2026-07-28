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
    /// re-pose occurrences between live reloads, and it is what a
    /// <see cref="MateSet">mate solver</see> solves for.</summary>
    public Frame3d Frame { get; set; }

    /// <summary>
    /// Where this occurrence goes at explode factor 1, as a displacement in the
    /// <b>parent assembly's</b> coordinates (the same space <see cref="Frame"/> lives
    /// in). Null means "stay put". <see cref="Assembly.Flatten(double)"/> adds
    /// <c>factor × ExplodeOffset</c> to <see cref="Frame"/>'s origin before composing,
    /// so a scalar 0 → 1 animates the whole explode and nested offsets compose:
    /// a sub-assembly moves as a unit and its own occurrences move within it.
    /// <para>Set it yourself for a designed exploded view, or let
    /// <see cref="Assembly.AutoExplode"/> derive one from the geometry.</para>
    /// </summary>
    public Vector3d? ExplodeOffset { get; set; }

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

    /// <summary>
    /// Resolves an occurrence path relative to this assembly ("clamp.2/bolt") to the
    /// chain of occurrences it names, outermost first. Paths use the same '/'-separated
    /// names <see cref="Flatten()"/> emits; a leading segment equal to this assembly's
    /// own name is accepted and skipped (unless a direct occurrence shares that name,
    /// in which case the occurrence wins), so a path copied from a
    /// <see cref="PartInstance.Path"/> resolves as-is. Throws naming the level that
    /// failed and what it does contain.
    /// </summary>
    public IReadOnlyList<Occurrence> ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An occurrence path must be non-empty.", nameof(path));
        string[] segments = path.Split('/');
        // Flatten() roots its paths at the assembly's name; accept that spelling too.
        int start = segments.Length > 1 && segments[0] == Name && _occurrences.All(o => o.Name != segments[0])
            ? 1 : 0;
        if (start == segments.Length)
            throw new ArgumentException(
                $"Occurrence path '{path}' names only the assembly itself, not an occurrence in it.", nameof(path));

        var chain = new List<Occurrence>();
        var current = this;
        for (int i = start; i < segments.Length; i++)
        {
            if (current is null)
                throw new ArgumentException(
                    $"Occurrence path '{path}': '{chain[^1].Name}' places a part, so nothing named " +
                    $"'{segments[i]}' can sit beneath it.", nameof(path));
            var next = current.Occurrences.FirstOrDefault(o => o.Name == segments[i])
                ?? throw new ArgumentException(
                    $"Occurrence path '{path}': assembly '{current.Name}' has no occurrence named " +
                    $"'{segments[i]}'. It contains: {string.Join(", ", current.Occurrences.Select(o => o.Name))}.",
                    nameof(path));
            chain.Add(next);
            current = next.SubAssembly;
        }
        return chain;
    }

    /// <summary>The occurrence an occurrence path names — the last link of
    /// <see cref="ResolvePath"/>.</summary>
    public Occurrence FindOccurrence(string path) => ResolvePath(path)[^1];

    /// <summary>Every path (rooted at this assembly's name, the
    /// <see cref="Flatten()"/> spelling) at which <paramref name="assembly"/> is placed
    /// in this subtree — the aliasing probe for cross-level mates: a sub-assembly's
    /// internal frames are ONE object however many times it is placed, so moving one
    /// moves every placement listed here.</summary>
    internal List<string> PlacementsOf(Assembly assembly)
    {
        var placements = new List<string>();
        CollectPlacements(assembly, Name, placements);
        return placements;
    }

    private void CollectPlacements(Assembly target, string prefix, List<string> into)
    {
        if (ReferenceEquals(this, target))
            into.Add(prefix);
        foreach (var occurrence in _occurrences)
            occurrence.SubAssembly?.CollectPlacements(target, $"{prefix}/{occurrence.Name}", into);
    }

    /// <summary>The path (relative, '/'-separated occurrence names) of the first
    /// placement of <paramref name="occurrence"/> in this subtree, or null when it is
    /// not placed here. Depth-first in occurrence order, so the answer is deterministic.</summary>
    internal string? PathTo(Occurrence occurrence)
    {
        foreach (var candidate in _occurrences)
        {
            if (ReferenceEquals(candidate, occurrence))
                return candidate.Name;
            if (candidate.SubAssembly?.PathTo(occurrence) is { } nested)
                return $"{candidate.Name}/{nested}";
        }
        return null;
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

    /// <summary>
    /// Flattens the tree to posed part instances (paths rooted at this assembly's name),
    /// composing occurrence frames depth-first.
    /// <para><paramref name="explode"/> scales every
    /// <see cref="Occurrence.ExplodeOffset"/> on the way down: 0 is the assembled
    /// position, 1 the fully exploded one, and anything between animates. It travels
    /// through the SAME flattening viewers and exporters already consume, so an exploded
    /// view is not a second code path — <see cref="Assembly.AutoExplode"/> derives the
    /// offsets, this composes them.</para>
    /// </summary>
    public IReadOnlyList<PartInstance> Flatten(double explode = 0) => Flatten(Frame3d.WorldXY, explode);

    /// <summary>Flattens with the whole assembly posed by <paramref name="root"/>.</summary>
    public IReadOnlyList<PartInstance> Flatten(in Frame3d root, double explode = 0)
    {
        var instances = new List<PartInstance>();
        FlattenInto(root, Name, instances, explode);
        return instances;
    }

    internal void FlattenInto(
        in Frame3d parentWorld, string parentPath, List<PartInstance> into, double explode = 0)
    {
        foreach (var occurrence in _occurrences)
        {
            var world = Posed(occurrence, explode).Then(parentWorld);
            string path = $"{parentPath}/{occurrence.Name}";
            if (occurrence.Part is { } part)
                into.Add(new PartInstance(part, world.ToMatrix() * part.Transform, path));
            else
                occurrence.SubAssembly!.FlattenInto(world, path, into, explode);
        }
    }

    /// <summary>An occurrence's pose with the explode displacement applied: the same
    /// frame, its origin moved by <c>explode × ExplodeOffset</c> in the parent's
    /// coordinates. Exactly the identity frame at factor 0 or with no offset — an
    /// un-exploded flatten is bit-for-bit what it always was.</summary>
    private static Frame3d Posed(Occurrence occurrence, double explode)
    {
        // Exact-zero semantic tests: "no offset set" and "not exploded" are decisions,
        // not measurements, and both must return the ORIGINAL frame untouched.
        if (occurrence.ExplodeOffset is not { } offset || explode == 0)
            return occurrence.Frame;
        var frame = occurrence.Frame;
        return Frame3d.FromOrthonormal(frame.Origin + offset * explode, frame.X, frame.Y);
    }

    /// <summary>World-space bounds of the flattened instances.</summary>
    public Aabb Bounds(MeshQuality? quality = null) => Bounds(Frame3d.WorldXY, quality);

    /// <summary>Bounds of the flattened instances with the assembly posed by
    /// <paramref name="root"/> (un-exploded).</summary>
    public Aabb Bounds(in Frame3d root, MeshQuality? quality = null)
    {
        var bounds = Aabb.Empty;
        foreach (var instance in Flatten(root))
            bounds = bounds.Union(instance.Bounds(quality));
        return bounds;
    }

    // ---- exploded views ----

    /// <summary>How far the outermost occurrence travels at factor 1 when
    /// <see cref="AutoExplode"/> is asked to choose: half the assembly's bounding-box
    /// diagonal. Big enough that fasteners clear their holes and stacked plates separate
    /// visibly, small enough that the exploded view still frames in one screen.</summary>
    public const double DefaultExplodeSpread = 0.5;

    /// <summary>
    /// Derives an <see cref="Occurrence.ExplodeOffset"/> for every occurrence from the
    /// assembly's own geometry, recursively. Three rules, in order:
    /// <list type="number">
    /// <item><description><b>The base body stays put.</b> The largest non-catalogue
    /// occurrence is the datum everything else comes off — the housing, the plate, the
    /// frame — and it gets no offset at all. An exploded view that moves the main body
    /// too is disorienting, and (the practical half) taking the datum from the ASSEMBLY
    /// CENTROID instead is wrong whenever the parts are spread out: the centroid sits in
    /// the empty middle and every part, base included, flies away from nothing.</description></item>
    /// <item><description><b>Hardware moves along its own axis.</b> A
    /// <see cref="HardwareComponent"/> body is modeled +Z out of the host, so the
    /// occurrence frame's Z IS the fastener axis — a screw backs straight out of its
    /// hole, which is the only direction that reads as "this came from there". Better
    /// than any centroid guess, and free, because the frame already knows it.</description></item>
    /// <item><description><b>Everything else moves radially away from the base</b>,
    /// scaled by how far out it already is: the outermost item travels the full spread and
    /// nearer ones proportionally less, so a stack keeps its order instead of
    /// interpenetrating. For a stack of plates the centres are strung along the stacking
    /// axis, so "radial" is axial there, which is the right answer.</description></item>
    /// </list>
    /// <para>Occurrences that already carry an explicit offset are left alone unless
    /// <paramref name="overwrite"/> says otherwise, so a hand-authored explode survives.
    /// Degenerate cases (an occurrence sitting exactly on the base's centre) get no
    /// offset rather than an arbitrary direction.</para>
    /// <para><b>Not for the render thread.</b> Deriving directions needs the instances'
    /// bounds, which mesh the parts (cached afterwards, so a pre-meshed scene is cheap) —
    /// the same rule <see cref="Scene.PreMesh"/> follows.</para>
    /// </summary>
    /// <param name="distance">Travel of the outermost occurrence at factor 1; 0 (the
    /// default) derives it from the bounds. An explicit distance is used at every level;
    /// a derived one is re-derived per sub-assembly from that sub-assembly's own size.</param>
    /// <param name="overwrite">Replace offsets that were set explicitly.</param>
    /// <param name="quality">Mesh quality for the bounds.</param>
    public void AutoExplode(double distance = 0, bool overwrite = false, MeshQuality? quality = null)
    {
        if (distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "An explode distance cannot be negative.");
        AutoExplode(distance, overwrite, quality, new HashSet<Assembly>());
    }

    private void AutoExplode(double distance, bool overwrite, MeshQuality? quality, HashSet<Assembly> visited)
    {
        // A sub-assembly placed twice is ONE object: derive its internal explode once.
        if (!visited.Add(this))
            return;

        var boxes = new Aabb[_occurrences.Count];
        var total = Aabb.Empty;
        for (int i = 0; i < _occurrences.Count; i++)
        {
            boxes[i] = LocalBounds(_occurrences[i], quality);
            total = total.Union(boxes[i]);
        }

        double spread = distance > 0 ? distance : total.IsEmpty ? 0 : total.Size.Length * DefaultExplodeSpread;
        int datum = Datum(boxes);
        var centre = datum >= 0 ? boxes[datum].Center : total.IsEmpty ? Vector3d.Zero : total.Center;

        // The radial scale: the furthest occurrence travels the full spread, the rest in
        // proportion. Comparing SQUARED radii keeps the loop free of square roots and the
        // guard below relative — an absolute epsilon on a squared quantity is exactly the
        // trap this codebase keeps re-learning.
        double furthestSquared = 0;
        for (int i = 0; i < _occurrences.Count; i++)
        {
            if (i != datum && !boxes[i].IsEmpty && _occurrences[i].Part?.Hardware is null)
                furthestSquared = Math.Max(furthestSquared, (boxes[i].Center - centre).LengthSquared);
        }

        for (int i = 0; i < _occurrences.Count; i++)
        {
            var occurrence = _occurrences[i];
            if (occurrence.SubAssembly is { } sub)
                sub.AutoExplode(distance, overwrite, quality, visited);
            if (!overwrite && occurrence.ExplodeOffset is not null)
                continue;

            occurrence.ExplodeOffset = i == datum
                ? null
                : Derived(occurrence, boxes[i], centre, furthestSquared, spread);
        }
    }

    /// <summary>The occurrence everything else explodes away from: the largest
    /// non-catalogue one by bounding-box diagonal (a designed body, never a fastener),
    /// or −1 when there is none. Ties go to the first, which is the order the design
    /// added them in.</summary>
    private int Datum(Aabb[] boxes)
    {
        int datum = -1;
        double largest = 0;
        for (int i = 0; i < _occurrences.Count; i++)
        {
            if (boxes[i].IsEmpty || _occurrences[i].Part?.Hardware is not null)
                continue;
            double size = boxes[i].Size.LengthSquared;
            if (size > largest)
            {
                largest = size;
                datum = i;
            }
        }
        return datum;
    }

    private static Vector3d? Derived(
        Occurrence occurrence, in Aabb box, in Vector3d centre, double furthestSquared, double spread)
    {
        if (spread <= 0)
            return null;

        // Rule 2: a catalogue component backs out along its own axis (its frame's +Z is
        // out of the host by the component local-frame convention).
        if (occurrence.Part?.Hardware is not null)
            return occurrence.Frame.Z * spread;

        // Rule 3: radial from the base, scaled by how far out the occurrence already sits.
        if (box.IsEmpty || furthestSquared <= 0)
            return null;
        var radial = box.Center - centre;
        double radiusSquared = radial.LengthSquared;
        // Scale-free degeneracy guard: an occurrence within 1e-9 (relative to the
        // furthest one) of the base's centre has no meaningful radial direction.
        // Squared on both sides so the comparison stays dimensionally honest.
        if (radiusSquared <= furthestSquared * 1e-18)
            return null;
        // direction × (spread × radius / furthest) collapses to one scale of `radial`,
        // so the furthest occurrence travels exactly `spread` and the rest in proportion.
        return radial * (spread / Math.Sqrt(furthestSquared));
    }

    /// <summary>One occurrence's bounds in THIS assembly's coordinates (un-exploded).</summary>
    private static Aabb LocalBounds(Occurrence occurrence, MeshQuality? quality) =>
        occurrence.Part is { } part
            ? part.Bounds(occurrence.Frame.ToMatrix() * part.Transform, quality)
            : occurrence.SubAssembly!.Bounds(occurrence.Frame, quality);
}
