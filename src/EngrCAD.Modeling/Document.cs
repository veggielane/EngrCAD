using System.Runtime.ExceptionServices;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

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
    /// <summary>The part's display name, unique among a tab's loose parts and assemblies.
    /// Rename through <see cref="Scene.Rename"/>, which owns that uniqueness invariant —
    /// a <see cref="Part"/> does not know which tab holds it.</summary>
    public string Name { get; private set; }

    /// <summary>The geometry the part was created from (Shape, BrepSolid, HalfEdgeMesh,
    /// or Sdf). Fixed for directly built parts; a part with a <see cref="History"/> may
    /// swap in a freshly regenerated body via <see cref="Regenerate"/>.</summary>
    public object Geometry { get; private set; }

    /// <summary>The parametric history this part was regenerated from, when it came
    /// from one (<see cref="FeatureHistory.ToPart"/>); null for directly built parts.
    /// Its presence makes <see cref="ConstructionTree"/> show features instead of the
    /// resulting shape graph.</summary>
    public FeatureHistory? History { get; }

    /// <summary>
    /// This part's named parameter sets and which one is active — <b>one
    /// <see cref="History"/>, N configurations</b> (an M4…M12 family of one bracket).
    /// Non-null exactly when the part HAS a history: a configuration is a set of
    /// <c>[Param]</c> values, so a part built directly from geometry has nothing to
    /// configure. Empty until something is added, and a document that adds nothing saves
    /// byte-identically to one that never heard of configurations.
    /// </summary>
    public ConfigurationSet? Configurations { get; }

    /// <summary>
    /// The catalogue item this part IS, when it came from one
    /// (<see cref="HardwareComponent.ToPart"/>); null for designed parts. Two things
    /// read it: a <see cref="Bom">bill of materials</see> (a hardware line carries the
    /// component, so a purchasing view can reach its designation and dimensions), and
    /// the default explode direction (<see cref="Assembly.AutoExplode"/> moves a
    /// fastener along its OWN axis, which its local frame already knows, instead of
    /// radially from the assembly centre).
    /// </summary>
    public HardwareComponent? Hardware { get; internal set; }

    /// <summary>
    /// What this part is made of — a <see cref="Material"/> from <see cref="Materials"/> or
    /// one the design builds. Null means unstated, which is the default and stays legal:
    /// nothing here requires a material.
    ///
    /// <para>Three things read it. <b>Mass properties</b> take their density from it, so
    /// <c>part.MassProperties()</c> and <c>scene.AllInstances.MassProperties()</c> need no
    /// density argument (the explicit overloads remain, for parts with no material).
    /// <b>The bill of materials</b> shows the name, and optionally the mass. <b>The palette</b>
    /// takes the material's <see cref="Material.Color"/> as this part's default color, if it
    /// states one — a material color does NOT consume a palette slot, so attaching a material
    /// to one part never re-colors the others.</para>
    ///
    /// <para><b>The density is in tonne/mm³</b>, the consistent mm/N/MPa/tonne system
    /// <see cref="ModelUnits"/> states for the whole repository — steel is 7.85e-9 — so a
    /// mass read from it is in tonnes and <see cref="ModelUnits.MassToGrams"/> is how a
    /// report prints it. The same <see cref="Material"/> object drives an FEA solve, which
    /// is the point of there being exactly one type.</para>
    /// </summary>
    public Material? Material { get; set; }

    /// <summary>
    /// The stock length this part is cut from, in millimetres — null (the default) for
    /// parts that are not cut from stock. <see cref="Weldment"/> stamps it on every
    /// frame member (the member's exact overall axial extent after trimming, so a
    /// mitred end counts to its longest point), and <see cref="BomLine.CutLength"/>
    /// projects it, which is what makes a <see cref="Bom"/> double as a cut list.
    /// The same follow-the-part pattern as <see cref="Material"/>: no BOM record had
    /// to change, and a part stating no cut length prints nothing.
    /// </summary>
    public double? CutLength { get; set; }

    /// <summary>Display color; when null, the tab assigns the material's color if it has
    /// one, else the next palette color on add.</summary>
    public PartColor? Color { get; set; }

    /// <summary>How viewers draw this part (shaded, wireframe, or translucent).
    /// Viewers may also change it interactively (per-part cycler in the model tree).</summary>
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Shaded;

    /// <summary>
    /// Whether a viewer's section planes cut this part (default true). Setting it false
    /// makes the part render — and pick — whole inside a cutaway, which is the drafting
    /// convention every standard shares: shafts, bolts, nuts, washers, keys, pins and
    /// ribs are drawn UNSECTIONED in a section view, because cutting a solid fastener
    /// lengthwise shows nothing and only clutters the section. It also gives assemblies
    /// the "cut the housing, keep the internals" view for free. An exempt part is not
    /// clipped, gets no cut-material shading, and contributes no section isolines (it
    /// has no cut face to draw them on).
    /// </summary>
    public bool ClippedBySection { get; set; } = true;

    // ---- debug display modifiers (the OpenSCAD #/%/!/* analog, part-level) ----

    /// <summary>Debug modifier (OpenSCAD <c>*</c> disable): the part is not rendered
    /// and not exported. Unlike removing it from the scene, it keeps its tree row (a
    /// viewer may re-show it) and its palette color. See <see cref="DebugFilter"/> for
    /// the rules viewers and exporters share.</summary>
    public bool Hidden { get; set; }

    /// <summary>Debug modifier (OpenSCAD <c>%</c> background): rendered translucent
    /// for reference, but EXCLUDED from geometry exports — scaffolding you want to see
    /// but never print. <see cref="EffectiveDisplayMode"/> resolves it.</summary>
    public bool Ghost { get; set; }

    /// <summary>Debug modifier (OpenSCAD <c>!</c> root): when ANY part in scope has
    /// this set, only isolated parts are shown/exported. Scope is the tab in the
    /// viewer and the scene for headless render/export (<see cref="DebugFilter"/>).</summary>
    public bool Isolated { get; set; }

    /// <summary>What a renderer should draw this part as: <see cref="DisplayMode"/>,
    /// with <see cref="Ghost"/> forcing <see cref="Modeling.DisplayMode.Translucent"/>.
    /// Every render path (window, offscreen, web) reads THIS, never the raw mode, so
    /// ghosting cannot fork between front ends.</summary>
    public DisplayMode EffectiveDisplayMode => Ghost ? DisplayMode.Translucent : DisplayMode;

    public Matrix4d Transform { get; set; } = Matrix4d.Identity;

    private readonly Lock _meshLock = new();
    private HalfEdgeMesh? _mesh;

    // ---- annotations (PMI) ----
    private readonly List<Annotation> _annotationList = [];
    private (IReadOnlyList<ResolvedAnnotation>? Resolved, string? Error)? _resolvedAnnotations;

    // ---- simulation results (fields on the display mesh's vertices) ----
    private readonly List<MeshField> _results = [];
    private readonly List<ResultSequence> _resultSequences = [];

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
        : this(name, (object)body, color, transform)
    {
        History = history;
        Configurations = new ConfigurationSet(this);
    }

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

    /// <summary>Sets <see cref="Material"/> and returns the part, so a material can be
    /// stated in the same expression that adds it: <c>scene.Add(new Part("plate",
    /// shape).Of(Materials.Aluminium6061))</c>.</summary>
    public Part Of(Material? material)
    {
        Material = material;
        return this;
    }

    /// <summary>
    /// Re-runs this part's <see cref="History"/> and, when the regeneration fully
    /// succeeds, swaps the fresh body in as <see cref="Geometry"/> and clears every
    /// derived cache (display mesh, cached B-Rep/SDF lowerings, feature edges,
    /// resolved annotations, construction tree) so the next consumer sees the edited
    /// model. This is the seam parameter editing goes through — a host sets a
    /// <c>[Param]</c> (or toggles <see cref="Feature.Suppressed"/>) and calls this.
    /// <para><b>A failed regeneration changes nothing here.</b>
    /// <see cref="FeatureHistory.Regenerate"/> itself keeps the last good prefix and
    /// skips the rest; this part additionally keeps its previous complete geometry, so
    /// a bad parameter value never leaves a half-regenerated body on screen. The
    /// returned <see cref="RegenerationResult"/> names the failing feature either way.</para>
    /// <para>Viewers that uploaded GPU buffers from the old mesh must republish after a
    /// successful call — the caches are per part, not per consumer.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The part has no history.</exception>
    public RegenerationResult Regenerate()
    {
        if (History is null)
            throw new InvalidOperationException(
                $"Part '{Name}' has no feature history to regenerate — it was built directly from geometry.");

        // Outside the locks: feature Apply bodies can be arbitrarily slow, and they
        // read nothing from this part.
        var result = History.Regenerate();
        if (!result.Succeeded || result.Body is not { } body)
            return result;

        lock (_meshLock)
        {
            Geometry = body;
            Volatile.Write(ref _mesh, null);   // pairs with HasMesh's lock-free probe
            _meshSegments = 0;                 // the refinement ratchet restarts with the mesh
            _solid = null;
            _solidLowered = false;
            _solidError = null;
            _sdf = null;
            _sdfLowered = false;
            _sdfError = null;
            _featureEdges = null;
            _resolvedAnnotations = null;   // selector dimensions re-measure the new body
        }
        lock (_constructionLock)
        {
            // Feature rows carry [Param] values in their labels, so the tree is stale too.
            _constructionTree = null;
            _constructionTreeBuilt = false;
        }
        return result;
    }

    // ---- the exact solid, lowered ONCE per part ----
    private BrepSolid? _solid;
    private bool _solidLowered;
    private Exception? _solidError;

    /// <summary>
    /// This part's geometry as an exact B-Rep, or null when it has none (an SDF or
    /// mesh part, a <see cref="Shape"/> with no B-Rep route, or a lowering that
    /// failed). <b>Lowered at most once per part and cached</b>: the display mesh, the
    /// feature-edge overlay, selector-based annotations, construction previews, and
    /// STEP export all take it from here, so a heavy graph is compiled once instead of
    /// once per consumer. Like <see cref="GetMesh"/> this belongs off the render
    /// thread — <see cref="Scene.PreMesh"/> primes it.
    /// </summary>
    public BrepSolid? TryGetSolid()
    {
        lock (_meshLock)
            return SolidCore();
    }

    /// <summary><see cref="TryGetSolid"/> with the lock already held.</summary>
    private BrepSolid? SolidCore()
    {
        if (_solidLowered)
            return _solid;
        _solidLowered = true;
        try
        {
            _solid = Geometry switch
            {
                BrepSolid direct => direct,
                Shape shape when shape.CanConvertTo(TargetRep.Brep) => shape.ToBrep(),
                _ => null,
            };
        }
        catch (Exception exception)
        {
            // A lowering that CanConvertTo accepted but that failed anyway (a boolean
            // the splitter cannot do, a validation guard). Remembered rather than
            // rethrown here: GetMesh replays it verbatim, GetFeatureEdges falls back
            // to mesh dihedrals, and neither pays for the failed lowering twice.
            _solidError = exception;
            _solid = null;
        }
        return _solid;
    }

    // ---- the distance field, lowered ONCE per part (the implicit twin of the above) ----
    private Sdf? _sdf;
    private bool _sdfLowered;
    private string? _sdfError;

    /// <summary>
    /// This part's geometry as a signed distance field — the implicit counterpart of
    /// <see cref="TryGetSolid"/>, and cached exactly the same way: an <see cref="Sdf"/>
    /// part hands back its own field, a <see cref="Shape"/> with an implicit route is
    /// lowered <b>at most once</b> (bridged lowerings can build a <c>MeshSdf</c>, which
    /// is far too expensive to repeat), and everything else returns false. A lowering
    /// that throws is remembered as a diagnostic rather than rethrown per caller, so a
    /// consumer that cannot show the field says so once instead of crashing or retrying.
    /// Consumers: the viewer's section-plane isoline overlay. Like <see cref="GetMesh"/>
    /// this belongs off the render thread — <see cref="Scene.PreMesh"/> primes it.
    /// </summary>
    /// <param name="sdf">The field, or null when the part has no implicit route.</param>
    /// <param name="error">Null unless a lowering was attempted and failed.</param>
    /// <returns>True when <paramref name="sdf"/> is non-null.</returns>
    public bool TryGetSdf(out Sdf? sdf, out string? error)
    {
        lock (_meshLock)
        {
            if (!_sdfLowered)
            {
                _sdfLowered = true;
                try
                {
                    _sdf = Geometry switch
                    {
                        Sdf direct => direct,
                        Shape shape when shape.CanConvertTo(TargetRep.Implicit) => shape.ToImplicit(),
                        _ => null,
                    };
                }
                catch (Exception e)
                {
                    _sdfError = $"Part '{Name}': implicit lowering failed ({e.GetType().Name}: {e.Message})";
                    _sdf = null;
                }
            }
            sdf = _sdf;
            error = _sdfError;
            return sdf is not null;
        }
    }

    /// <summary>
    /// Whether the display mesh has already been produced — a non-blocking probe (it
    /// takes no lock, so it never waits behind a mesh in flight on another thread).
    /// Hosts that mesh lazily use it to decide whether a part can be shown/inspected
    /// right now or is still being prepared; false may lag a just-finished mesh by an
    /// instant, which only ever costs one extra "not ready yet" answer.
    /// </summary>
    public bool HasMesh => Volatile.Read(ref _mesh) is not null;

    /// <summary>
    /// The display mesh, produced on first call (Shapes and B-Reps by tessellating the
    /// cached <see cref="TryGetSolid"/> solid, other Shapes via their best route, SDFs
    /// via Surface Nets, meshes as-is) and cached — the first caller's
    /// <paramref name="quality"/> wins. Scenes pre-mesh all parts with their own
    /// quality, so parts shown through a scene use the scene's settings.
    /// </summary>
    public HalfEdgeMesh GetMesh(MeshQuality? quality = null) => GetMesh(quality, null);

    /// <summary>
    /// <see cref="GetMesh(MeshQuality?)"/> with progress reporting and cooperative
    /// cancellation for the routes that support them.
    /// <para><b>B-Rep <i>lowering</i> is the one step that always runs to
    /// completion.</b> Its result is cached inside <see cref="TryGetSolid"/>, and
    /// abandoning one mid-flight would leave that cache claiming a lowering it never
    /// produced. Everything downstream of it is safely cancellable and does observe
    /// <paramref name="progress"/>: Surface Nets polygonization on the SDF route, and
    /// <see cref="BRepTessellator.Tessellate(BrepSolid, int, int, ProgressCancel?)"/> on
    /// the B-Rep one — tessellating an already-cached solid throws nothing away, since a
    /// revisit re-tessellates from the cache. A host that stops early still gets the
    /// lowering it paid for.</para>
    /// </summary>
    public HalfEdgeMesh GetMesh(MeshQuality? quality, ProgressCancel? progress)
    {
        lock (_meshLock)
        {
            if (_mesh is not null)
                return _mesh;
            var q = quality ?? MeshQuality.Default;
            if (Geometry is HalfEdgeMesh direct)
                return _mesh = direct;
            if (Geometry is Sdf sdf)
                return _mesh = SurfaceNets.Polygonize(sdf, q.SdfResolution, progress, q.SurfaceNets);

            // B-Rep-representable geometry (a BrepSolid part or a Shape with a B-Rep
            // route): tessellate the ONE cached solid. This is exactly what
            // Shape.ToMesh does for such a graph — it just re-lowered every time.
            if (SolidCore() is { } solid)
            {
                // Fixed counts, or the adaptive TessellationQuality resolution — the
                // SAME resolution GetFeatureEdges uses, so overlay and fill agree.
                var (segmentsPerCircle, curveSamples) = q.ResolveSegments(solid);
                _meshSegments = segmentsPerCircle;
                return _mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples, progress);
            }
            if (_solidError is { } error)
                ExceptionDispatchInfo.Capture(error).Throw();   // same failure ToMesh would raise

            if (Geometry is Shape shape)
                return _mesh = shape.ToMesh(quality);   // implicit/mesh route
            throw new InvalidOperationException($"Unknown geometry type {Geometry.GetType().Name}.");
        }
    }

    /// <summary>Segments per circle the cached B-Rep mesh was tessellated at (0 when the
    /// part has no mesh, or was not produced by tessellation). The ratchet
    /// <see cref="RefineMesh"/> compares against — see its remarks.</summary>
    private int _meshSegments;

    /// <summary>
    /// Re-tessellates the cached display mesh at a FINER quality, and only ever finer —
    /// the deliberate re-mesh entry point behind the viewer's camera-adaptive display
    /// quality. Returns true when a new mesh was produced (the caller must then republish
    /// whatever it uploaded from the old one).
    /// <para><b>It is a ratchet.</b> The refinement runs only when
    /// <paramref name="quality"/> resolves to strictly MORE segments per circle than the
    /// cached mesh already carries, so a request to coarsen — a camera pulling back — is
    /// declined rather than obeyed: a part that visibly loses detail on zoom-out reads as
    /// a bug even where a criterion is working, and the mesh already in hand is the better
    /// answer at no cost. That makes "never coarser than this session started" a property
    /// of this method rather than of any one caller's bookkeeping.</para>
    /// <para><b>Only B-Rep-routed parts refine.</b> A raw <c>HalfEdgeMesh</c> part has no
    /// finer form to produce, and an SDF's <see cref="MeshQuality.SdfResolution"/> is a
    /// grid resolution rather than a per-radius quantity (the same reason
    /// <see cref="TessellationQuality"/> leaves it alone); both return false. A part with
    /// no mesh yet also returns false — producing the FIRST mesh is the display loader's
    /// job at the session quality, and this only sharpens one that exists.</para>
    /// <para>The cached B-Rep lowering is criterion-independent and is deliberately kept,
    /// which is what makes this affordable: a refinement is the tessellate half only. The
    /// feature-edge overlay is REBUILT here at the same quality — dropping it and leaving
    /// it to whoever asks next would resolve it against THAT caller's quality, and the
    /// smooth exact edge would detach from the finer fill it outlines. Results,
    /// annotations and the construction tree are untouched, none of them being functions
    /// of the display density. A tessellation that throws leaves the cached mesh, its
    /// count and its overlay exactly as they were.</para>
    /// <para>Belongs off the render thread, exactly like
    /// <see cref="GetMesh(MeshQuality?, ProgressCancel?)"/>.</para>
    /// </summary>
    public bool RefineMesh(MeshQuality quality, ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(quality);
        lock (_meshLock)
        {
            if (_mesh is null || _meshSegments <= 0)
                return false;   // nothing cached to sharpen (or not a tessellated mesh)
            if (SolidCore() is not { } solid)
                return false;   // mesh/SDF part: no finer tessellation exists

            var (segmentsPerCircle, curveSamples) = quality.ResolveSegments(solid);
            if (segmentsPerCircle <= _meshSegments)
                return false;   // the ratchet: never coarser, and never for nothing

            var refined = BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples, progress);
            _meshSegments = segmentsPerCircle;
            Volatile.Write(ref _mesh, refined);   // pairs with HasMesh's lock-free probe

            // One criterion drives fill AND overlay, so the edges are rebuilt HERE at the
            // same quality rather than left null for whoever asks next: a later
            // GetFeatureEdges would resolve against ITS caller's quality, which is the
            // session's, and the smooth exact edge would detach from the finer fill.
            _featureEdges = null;
            GetFeatureEdges(quality);
            return true;
        }
    }

    /// <summary>Segments per circle the cached display mesh was tessellated at, or 0 when
    /// it has none (or came from a route that states no such count). Exposed so a host can
    /// report what the adaptive display quality has reached.</summary>
    public int MeshSegmentsPerCircle
    {
        get { lock (_meshLock) return _meshSegments; }
    }

    private readonly Lock _constructionLock = new();
    private ConstructionNode? _constructionTree;
    private bool _constructionTreeBuilt;

    /// <summary>
    /// How this part was built, as a row tree a viewer can expand: the ordered feature
    /// list when the part came from a <see cref="FeatureHistory"/>, otherwise the
    /// <see cref="Shape"/> operation graph, otherwise null (raw B-Rep/mesh/SDF parts
    /// carry no construction information). Built once and cached — a part's geometry is
    /// fixed at construction, so node references are stable and usable as preview-cache
    /// keys (<see cref="ConstructionPreviewCache"/>).
    /// <para>Guarded by its own lock, NOT the mesh lock: the tree is read from the
    /// geometry graph and needs no mesh, so a viewer building tree rows on the UI
    /// thread must never queue behind a part being meshed on a worker.</para>
    /// </summary>
    public ConstructionNode? ConstructionTree()
    {
        lock (_constructionLock)
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

    /// <summary>Detaches an annotation; false when it was not attached. Undo needs its
    /// position back, which is why <see cref="InsertAnnotation"/> exists beside it.</summary>
    public bool RemoveAnnotation(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        lock (_meshLock)
        {
            if (!_annotationList.Remove(annotation))
                return false;
            _resolvedAnnotations = null;
            return true;
        }
    }

    /// <summary>The index of an attached annotation, or −1.</summary>
    internal int IndexOfAnnotation(Annotation annotation)
    {
        lock (_meshLock)
            return _annotationList.IndexOf(annotation);
    }

    /// <summary>Re-attaches an annotation AT ITS OLD POSITION — the undo counterpart of
    /// <see cref="RemoveAnnotation"/>. Order is observable (the document file writes the
    /// list in order), so restoring it is what makes undo serialize identically.</summary>
    internal void InsertAnnotation(int index, Annotation annotation)
    {
        lock (_meshLock)
        {
            _annotationList.Insert(index, annotation);
            _resolvedAnnotations = null;
        }
    }

    /// <summary><see cref="Scene.Rename"/>'s writer: the uniqueness check lives on the
    /// scene, which is the only thing that can see every tab.</summary>
    internal void SetName(string name) => Name = name;

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

    // ---- simulation results ----

    /// <summary>
    /// The simulation results attached to this part — scalar or vector
    /// <see cref="MeshField"/>s over its <b>display mesh's vertices</b>
    /// (<c>GetMesh().VertexCount</c> values, in vertex-index order).
    /// <para>Results live here rather than in a viewport so they survive tab and scene
    /// plumbing, export with the document, and are visible to headless renders and the
    /// MCP server. Nothing in this class evaluates or validates them: attaching a result
    /// is free and never meshes anything (which is what keeps
    /// <see cref="Scene.PreMesh"/> free to run in parallel), and the vertex-count check
    /// happens where a consumer actually has the mesh in hand — reported by name, never
    /// silently ignored.</para>
    /// </summary>
    public IReadOnlyList<MeshField> Results
    {
        get
        {
            lock (_meshLock)
                return [.. _results];
        }
    }

    /// <summary>
    /// Attaches a result (chainable). A second result with the same
    /// <see cref="MeshField.Name"/> REPLACES the first, in place, so re-running a solve
    /// updates the display instead of accumulating stale twins under one name — and
    /// <see cref="FieldDisplay"/>, which refers to results by name, keeps pointing at
    /// the live one.
    /// </summary>
    public Part AddResult(MeshField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        lock (_meshLock)
        {
            int existing = _results.FindIndex(f => f.Name == field.Name);
            if (existing >= 0)
                _results[existing] = field;
            else
                _results.Add(field);
        }
        return this;
    }

    /// <summary>The attached result of that name, or null.</summary>
    public MeshField? Result(string name)
    {
        lock (_meshLock)
            return _results.Find(f => f.Name == name);
    }

    /// <summary>The time axes attached to this part's results — see
    /// <see cref="AddResultSequence"/>.</summary>
    public IReadOnlyList<ResultSequence> ResultSequences
    {
        get
        {
            lock (_meshLock)
                return [.. _resultSequences];
        }
    }

    /// <summary>The result sequence of that name, or null.</summary>
    public ResultSequence? Sequence(string name)
    {
        lock (_meshLock)
            return _resultSequences.Find(s => s.Name == name);
    }

    /// <summary>
    /// Publishes a transient run as results WITH their time axis (chainable): each
    /// step's field is attached under the derived name
    /// <see cref="ResultSequence.StepName"/> ("Temperature @ 0.5s"), and the
    /// <see cref="ResultSequence"/> records the order and the instants — the axis a
    /// saved document used to lose, and the input <c>FieldSequenceTrack.For</c> builds
    /// the playback from. The whole call validates BEFORE it mutates (a refused
    /// sequence leaves the part untouched), and a second sequence under the same name
    /// REPLACES the first, removing any of the old record's results the new one does
    /// not reuse — a re-run with different instants must not leave stale twins behind.
    /// </summary>
    public Part AddResultSequence(string name, IReadOnlyList<(MeshField Field, double Seconds)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var renamed = new List<MeshField>(steps.Count);
        var recorded = new List<(string ResultName, double Seconds)>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            var (field, seconds) = steps[i];
            if (field is null)
                throw new ArgumentException($"Step {i} carries no field.", nameof(steps));
            string stepName = ResultSequence.StepName(name, seconds);
            renamed.Add(new MeshField(stepName, field.Units, field.Components, field.Values, field.Association));
            recorded.Add((stepName, seconds));
        }
        var sequence = new ResultSequence(name, recorded);
        lock (_meshLock)
        {
            int existing = _resultSequences.FindIndex(s => s.Name == name);
            if (existing >= 0)
            {
                var kept = new HashSet<string>(recorded.Select(r => r.ResultName));
                foreach (var (oldName, _) in _resultSequences[existing].Steps)
                {
                    if (!kept.Contains(oldName))
                        _results.RemoveAll(f => f.Name == oldName);
                }
                _resultSequences[existing] = sequence;
            }
            else
            {
                _resultSequences.Add(sequence);
            }
            foreach (var field in renamed)
            {
                int at = _results.FindIndex(f => f.Name == field.Name);
                if (at >= 0)
                    _results[at] = field;
                else
                    _results.Add(field);
            }
        }
        return this;
    }

    /// <summary>Restores a loaded sequence RECORD without touching the results — the
    /// document file attaches the step fields separately.</summary>
    internal void RestoreResultSequence(ResultSequence sequence)
    {
        lock (_meshLock)
        {
            int existing = _resultSequences.FindIndex(s => s.Name == sequence.Name);
            if (existing >= 0)
                _resultSequences[existing] = sequence;
            else
                _resultSequences.Add(sequence);
        }
    }

    /// <summary>
    /// Which result colours this part, through which map, over what range, and whether
    /// the shape is displaced — null (the default) draws the part in its own colour with
    /// no field at all. See <see cref="Modeling.FieldDisplay"/>.
    /// </summary>
    public FieldDisplay? FieldDisplay { get; set; }

    /// <summary>
    /// Resolves <see cref="FieldDisplay"/> against <see cref="Results"/>: looks the
    /// names up, settles the range (an explicit one wins; otherwise the field's own),
    /// and checks that a deformation field really is a vector field.
    /// <para>Deliberately does NOT mesh: the vertex-count check belongs to the renderer
    /// that has the mesh, so this stays callable from a properties panel, the MCP server
    /// or a test with no GL and no tessellation. Returns false with a diagnostic naming
    /// the part and the missing result — a display that refers to a result an edit
    /// removed becomes a status message, never a crash.</para>
    /// </summary>
    public bool TryResolveFieldDisplay(out ResolvedFieldDisplay resolved, out string? error)
    {
        resolved = default;
        error = null;
        if (FieldDisplay is not { } display)
            return false;

        lock (_meshLock)
        {
            var field = _results.Find(f => f.Name == display.Field);
            if (field is null)
            {
                error = $"Part '{Name}': no result named '{display.Field}'"
                    + (_results.Count == 0
                        ? " (the part carries none)."
                        : $" (it has {string.Join(", ", _results.Select(f => $"'{f.Name}'"))}).");
                return false;
            }
            MeshField? deform = null;
            if (display.Deform is { } deformName)
            {
                deform = _results.Find(f => f.Name == deformName);
                if (deform is null)
                {
                    error = $"Part '{Name}': no result named '{deformName}' to deform by.";
                    return false;
                }
                if (!deform.IsVector)
                {
                    error = $"Part '{Name}': result '{deformName}' is a scalar field; " +
                        "a deformed shape needs a vector (displacement) field.";
                    return false;
                }
            }
            var range = display.Range ?? field.Range;
            if (range.IsEmpty)
            {
                error = $"Part '{Name}': result '{field.Name}' has no finite values to map.";
                return false;
            }
            if (display.LogScale && !(range.Min > 0))
            {
                error = $"Part '{Name}': a log-scale display needs a strictly positive "
                    + $"range, and '{field.Name}' spans [{range.Min:G4}, {range.Max:G4}]. "
                    + "State an explicit positive Range, or drop LogScale.";
                return false;
            }
            resolved = new ResolvedFieldDisplay(
                field, range, display.ColorMap, deform, display.DeformScale,
                display.ShowUndeformed, display.LogScale);
            return true;
        }
    }

    /// <summary>The selector target: the part's shared cached solid (see
    /// <see cref="TryGetSolid"/>) — annotations do NOT lower the geometry again.</summary>
    private BrepSolid LowerForAnnotations() => SolidCore()
        ?? throw new InvalidOperationException(
            _solidError is { } error
                ? $"selector-based annotations need an exact solid, and this part's " +
                  $"geometry failed to lower: {error.GetType().Name}: {error.Message}"
                : $"selector-based annotations need B-Rep-representable geometry " +
                  $"(this part holds {Geometry.GetType().Name}).");

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
            // The SHARED cached solid — no second lowering (this used to be a Shape
            // part's second full B-Rep compile).
            if (SolidCore() is { } solid)
            {
                try
                {
                    // Under an adaptive quality the overlay uses EXACTLY the mesh's
                    // resolved counts (one criterion drives both, so the smooth exact
                    // edge cannot detach from the faceted fill it outlines); with fixed
                    // counts it keeps the deliberately finer display resolution.
                    var (segmentsPerCircle, curveSamples) = q.Tessellation is { } adaptive
                        ? adaptive.ResolveFor(solid)
                        : (Math.Max(96, q.SegmentsPerCircle), Math.Max(48, q.CurveSamples));
                    return _featureEdges = BrepFeatureEdges.Extract(solid, segmentsPerCircle, curveSamples);
                }
                catch
                {
                    // Any extraction hiccup falls back to the mesh route below.
                }
            }
            return _featureEdges = MeshFeatureEdges.Extract(GetMesh(quality));
        }
    }

    /// <summary>
    /// Produces everything a viewer needs from this part off the render thread: the
    /// display mesh, the feature-edge overlay, and the resolved annotations — one
    /// part's worth of <see cref="Scene.PreMesh"/>, exposed per part so a host can
    /// prepare incrementally (the viewer meshes only the tab being viewed, publishing
    /// parts as they land). Idempotent: every product is cached, so a second call costs
    /// nothing and a prepared part displays instantly.
    /// <para><paramref name="progress"/> is reported over this part alone (0 → 1); only
    /// the SDF route reports intermediate fractions or observes cancellation — see
    /// <see cref="GetMesh(MeshQuality?, ProgressCancel?)"/>.</para>
    /// </summary>
    public void Prepare(MeshQuality? quality = null, ProgressCancel? progress = null)
    {
        GetMesh(quality, progress);
        // Feature edges and selector annotations both read the ONE cached solid the
        // mesh already lowered, so this is extraction only — but it must still happen
        // here rather than lazily inside a GL upload.
        GetFeatureEdges(quality);
        if (Annotations.Count > 0)
            TryResolveAnnotations(out _, out _);
        progress?.Report(1);
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
        EnsureColor(part);
        _parts.Add(part);
        return part;
    }

    /// <summary>Removes a loose part from this tab (false when it is not here). The
    /// part object itself — results, annotations, history — is untouched; other tabs
    /// and assemblies referencing it keep it.</summary>
    public bool Remove(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return _parts.Remove(part);
    }

    /// <summary>This tab's index of a loose part, −1 when absent.</summary>
    public int IndexOf(Part part) => _parts.IndexOf(part);

    /// <summary>Re-inserts a part at a stated index — what makes an undone removal come
    /// back in PLACE rather than appended (the serializer writes parts in list order,
    /// so position is part of the document's identity).</summary>
    internal void Insert(int index, Part part)
    {
        if (_parts.Any(p => p.Name == part.Name) || _assemblies.Any(a => a.Name == part.Name))
            throw new ArgumentException($"Tab '{Name}' already contains a part named '{part.Name}'.", nameof(part));
        EnsureColor(part);
        _parts.Insert(index, part);
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
            EnsureColor(part);
        _assemblies.Add(assembly);
        return assembly;
    }

    /// <summary>
    /// The tab flattened for display: loose parts first (world = the part's own
    /// transform, path = its name), then each assembly's instances depth-first
    /// (paths like "gearbox/stack.2/bolt"). This ordered list is the seam viewers
    /// consume — instance index i here is instance index i in the viewport.
    /// <para><paramref name="explode"/> (0 assembled → 1 fully exploded) scales the
    /// assemblies' <see cref="Occurrence.ExplodeOffset"/>s; loose parts never move, since
    /// they belong to no assembly and so have nothing to explode away from. The instance
    /// COUNT and ORDER are independent of it, which is what lets a viewer animate the
    /// factor without re-uploading a single buffer.</para>
    /// </summary>
    public IReadOnlyList<PartInstance> Instances(double explode = 0)
    {
        AssignColors();
        var instances = new List<PartInstance>(_parts.Count);
        foreach (var part in _parts)
            instances.Add(new PartInstance(part, part.Transform, part.Name));
        foreach (var assembly in _assemblies)
            assembly.FlattenInto(Frame3d.WorldXY, assembly.Name, instances, explode);
        return instances;
    }

    /// <summary>The tab flattened with a PER-OCCURRENCE explode factor (see
    /// <see cref="Assembly.Flatten(Func{Occurrence, double})"/>) — the sequenced-explode
    /// substrate. Same walk, same instance count and order as the scalar overload.</summary>
    public IReadOnlyList<PartInstance> Instances(Func<Occurrence, double> explodeOf)
    {
        ArgumentNullException.ThrowIfNull(explodeOf);
        AssignColors();
        var instances = new List<PartInstance>(_parts.Count);
        foreach (var part in _parts)
            instances.Add(new PartInstance(part, part.Transform, part.Name));
        foreach (var assembly in _assemblies)
            assembly.FlattenInto(Frame3d.WorldXY, assembly.Name, instances, explodeOf);
        return instances;
    }

    /// <summary>
    /// Palette colors for parts that arrived AFTER their container did — a part added
    /// to an assembly after <see cref="Add(Assembly)"/> has no color until the tab next
    /// flattens, so every flatten sweeps for colorless parts first.
    /// <para><b>The color-stability rule</b>: a color, once assigned, never changes
    /// (assignment is <c>??=</c> and the palette cursor only advances), and latecomers
    /// take the NEXT palette entries in the tab's own display order — loose parts
    /// first, then each assembly's distinct parts depth-first. So adding a part later
    /// can never reshuffle an existing part's color; it can only consume a fresh one.</para>
    /// </summary>
    private void AssignColors()
    {
        foreach (var part in _parts)
            EnsureColor(part);
        foreach (var assembly in _assemblies)
        {
            foreach (var part in assembly.DistinctParts())
                EnsureColor(part);
        }
    }

    /// <summary>
    /// The one place a part's default color is decided: its <see cref="Part.Material"/>'s
    /// color if the material states one, else the next palette entry.
    ///
    /// <para><b>A material color does not consume a palette slot.</b> The cursor advances
    /// only when the palette is actually read, so giving one part a colored material leaves
    /// every other part's color exactly where it was — the same stability rule
    /// <see cref="AssignColors"/> documents, extended to the new source. (It is also why no
    /// entry in <see cref="Materials"/> carries a color: assigning a catalogue material to a
    /// part moves no pixels.)</para>
    /// </summary>
    private void EnsureColor(Part part)
    {
        if (part.Color is not null)
            return;
        part.Color = part.Material?.Color ?? Palette.Cycle[_nextColor++ % Palette.Cycle.Length];
    }

    /// <summary>Derives explode offsets for every assembly in this tab
    /// (<see cref="Assembly.AutoExplode"/>). Off the render thread: it needs the parts'
    /// bounds.</summary>
    public void AutoExplode(double distance = 0, bool overwrite = false, MeshQuality? quality = null)
    {
        foreach (var assembly in _assemblies)
            assembly.AutoExplode(distance, overwrite, quality);
    }

    /// <summary>True when any assembly in this tab has something to explode — the cheap
    /// probe a viewer uses to decide whether to offer the control at all.</summary>
    public bool HasAssemblies => _assemblies.Count > 0;

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

    /// <summary>
    /// Prepares just this tab's parts for display (<see cref="Part.Prepare"/> each
    /// distinct part, in order) — the on-demand sibling of <see cref="Scene.PreMesh"/>
    /// for hosts that mesh a tab when it is first viewed instead of meshing the whole
    /// document up front. Idempotent, so a second visit costs nothing.
    /// <para><paramref name="progress"/> reports the fraction of this tab's parts done
    /// (finer within a part where the route supports it) and is polled between parts as
    /// well as within them. What a cancelled part keeps is the work that cannot be
    /// abandoned safely: B-Rep <i>lowering</i> always runs to completion and stays cached,
    /// while polygonization and tessellation stop where they are — see
    /// <see cref="Part.GetMesh(MeshQuality?, ProgressCancel?)"/>. So a revisit never
    /// repeats the expensive half. Cancellation surfaces as
    /// <see cref="OperationCanceledException"/>.</para>
    /// </summary>
    public void PreMesh(MeshQuality? quality = null, ProgressCancel? progress = null)
    {
        var parts = AllParts.ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            progress?.ThrowIfCancelled();
            int index = i;
            var perPart = progress is null
                ? null
                : new ProgressCancel(
                    () => progress.CancelRequested,
                    fraction => progress.Report((index + fraction) / parts.Count));
            parts[i].Prepare(quality, perPart);
        }
        progress?.Report(1);
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
    public IEnumerable<PartInstance> AllInstances => Instances();

    /// <summary>Every posed part instance across all tabs, at an explode factor
    /// (0 assembled → 1 fully exploded).</summary>
    public IEnumerable<PartInstance> Instances(double explode = 0) =>
        _tabs.SelectMany(t => t.Instances(explode));

    /// <summary>Every posed part instance across all tabs, with a per-occurrence
    /// explode factor (see <see cref="Assembly.Flatten(Func{Occurrence, double})"/>).</summary>
    public IEnumerable<PartInstance> Instances(Func<Occurrence, double> explodeOf) =>
        _tabs.SelectMany(t => t.Instances(explodeOf));

    /// <summary>Derives explode offsets for every assembly in the scene
    /// (<see cref="Assembly.AutoExplode"/>). Off the render thread: it needs bounds.</summary>
    public void AutoExplode(double distance = 0, bool overwrite = false, MeshQuality? quality = null)
    {
        foreach (var tab in _tabs)
            tab.AutoExplode(distance, overwrite, quality);
    }

    public Tab AddTab(string name)
    {
        if (_tabs.Any(t => t.Name == name))
            throw new ArgumentException($"The scene already contains a tab named '{name}'.", nameof(name));
        var tab = new Tab(name);
        _tabs.Add(tab);
        return tab;
    }

    /// <summary>Removes a tab (false when it is not here). Its parts are untouched —
    /// they may be shown by other tabs, and a part is deleted by ceasing to be
    /// referenced, never by an explicit destructor.</summary>
    public bool RemoveTab(Tab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _tabs.Remove(tab);
    }

    /// <summary>This scene's index of a tab, −1 when absent.</summary>
    public int IndexOf(Tab tab) => _tabs.IndexOf(tab);

    /// <summary>Re-inserts a tab at a stated index (the undone-removal rule: position is
    /// part of the document's identity).</summary>
    internal void InsertTab(int index, Tab tab)
    {
        if (_tabs.Any(t => t.Name == tab.Name))
            throw new ArgumentException($"The scene already contains a tab named '{tab.Name}'.", nameof(tab));
        _tabs.Insert(index, tab);
    }

    /// <summary>Adds a part to the default "Model" tab (created on first use).</summary>
    public Part Add(Part part)
    {
        var tab = _tabs.FirstOrDefault(t => t.Name == "Model") ?? AddTab("Model");
        return tab.Add(part);
    }

    /// <summary>
    /// Renames a part, enforcing the invariant a <see cref="Tab"/> owns: names are unique
    /// within a tab across its loose parts and assemblies. A part that appears only inside
    /// assemblies has no such constraint (occurrences carry their own names), so it renames
    /// freely.
    /// <para>The rename lives here rather than on <see cref="Part"/> because a part does
    /// not know which tabs hold it, and rather than on <see cref="Tab"/> because a part may
    /// be shown in several — the scene is the only object that can see the whole
    /// constraint.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, or it collides with another
    /// item in a tab that holds this part loose.</exception>
    public void Rename(Part part, string name)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Part name must be non-empty.", nameof(name));
        foreach (var tab in _tabs)
        {
            if (!tab.Parts.Contains(part))
                continue;
            if (tab.Parts.Any(p => !ReferenceEquals(p, part) && p.Name == name)
                || tab.Assemblies.Any(a => a.Name == name))
                throw new ArgumentException(
                    $"Tab '{tab.Name}' already contains an item named '{name}'.", nameof(name));
        }
        part.SetName(name);
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
    /// the scene did not choose an explicit quality (see <see cref="ResolveQuality"/>).
    /// <para>The mesh, the feature edges, and selector annotations of a B-Rep-backed
    /// part all come from the ONE solid <see cref="Part.TryGetSolid"/> caches, so this
    /// pass lowers each part at most once.</para>
    /// <para><b>Parts are primed in parallel.</b> They are independent by construction —
    /// every cache a part fills (<see cref="Part.TryGetSolid"/>, the display mesh, the
    /// feature edges, resolved annotations) lives on that part behind its own lock, and
    /// lowering a <see cref="Shape"/> graph builds fresh geometry rather than mutating
    /// the graph — so the result is identical to the sequential pass whatever the
    /// scheduling. A part that is instanced many times is still meshed exactly once
    /// (<see cref="AllParts"/> dedupes by reference), and one slow part no longer blocks
    /// every other part behind it.</para>
    /// <para>Failures stay deterministic too: each part's exception is captured in its
    /// own slot and the FIRST failure in scene order is rethrown with its original stack
    /// — the same exception the sequential pass surfaced, not a scheduling-dependent
    /// <see cref="AggregateException"/>.</para></summary>
    public void PreMesh(MeshQuality? fallback = null)
    {
        var quality = ResolveQuality(fallback);
        var parts = AllParts.ToArray();
        var failures = new Exception?[parts.Length];

        ParallelFor.Blocks(0, parts.Length, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                var part = parts[i];
                try
                {
                    part.GetMesh(quality);
                    // Prime the feature-edge cache too (extraction over the already-lowered
                    // solid): it must happen here, off the render thread, not lazily inside a
                    // GL upload.
                    part.GetFeatureEdges(quality);
                    // Pre-resolve annotations too: selector dimensions lower to B-Rep, which
                    // must not happen on the render thread. Failures are cached diagnostics
                    // (viewers surface them via TryResolveAnnotations), not exceptions here.
                    if (part.Annotations.Count > 0)
                        part.TryResolveAnnotations(out _, out _);
                }
                catch (Exception exception)
                {
                    // Own slot only — the ParallelFor determinism contract. Priming the
                    // remaining parts is harmless (each caches independently) and keeps
                    // the choice of which failure to report a matter of scene order.
                    failures[i] = exception;
                }
            }
        });

        foreach (var failure in failures)
        {
            if (failure is { } exception)
                ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
