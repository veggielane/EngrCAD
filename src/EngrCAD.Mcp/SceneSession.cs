using EngrCAD.Modeling;

namespace EngrCAD.Mcp;

/// <summary>
/// The live model behind an MCP session: the design program's scene factory plus the
/// scene it most recently produced. Tools read the scene through this, so
/// <c>reload</c> can swap in a freshly built one exactly as the <c>dotnet watch</c>
/// live loop does.
/// <para><b>Nothing is meshed here.</b> A scene is a graph of unevaluated geometry
/// until something asks for a display mesh, and most tools (<c>list_tabs</c>,
/// <c>list_parts</c>) never do — meshing a demo scene costs tens of seconds, which
/// would be paid on every server start. Geometry is produced per part on the tool
/// that needs it (<see cref="MeshOf"/>), or for the whole scene by
/// <see cref="PreMesh"/> when a screenshot or export genuinely needs all of it.</para>
/// </summary>
public sealed class SceneSession
{
    private readonly Func<Scene> _factory;
    private readonly MeshQuality? _fallbackQuality;
    private readonly Func<Scene, Viewer.Animation?>? _animationFactory;
    private readonly Lock _gate = new();
    private Scene _scene;
    private Viewer.Animation? _animation;
    private bool _animationBuilt;
    private int _generation;

    /// <summary>Wraps a scene factory (the same delegate <c>EngrCad.Run</c> takes).
    /// The factory is invoked once, now, to establish the initial scene.</summary>
    /// <param name="factory">Builds the scene; re-invoked by <see cref="Reload"/>.</param>
    /// <param name="fallbackQuality">Host-level mesh quality used only when the scene
    /// did not choose its own (<see cref="Scene.ResolveQuality"/>).</param>
    /// <param name="animationFactory">The host's <c>EngrCadOptions.Animation</c>, if any
    /// — what the <c>screenshot</c> tool's <c>t</c> parameter evaluates. Built lazily
    /// (track construction may mesh for bounds) and rebuilt after a reload.</param>
    public SceneSession(Func<Scene> factory, MeshQuality? fallbackQuality = null,
        Func<Scene, Viewer.Animation?>? animationFactory = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _fallbackQuality = fallbackQuality;
        _animationFactory = animationFactory;
        _scene = factory();
    }

    /// <summary>Wraps an already-built scene; <see cref="Reload"/> then rebuilds
    /// nothing and says so (useful for tests and static documents).</summary>
    public SceneSession(Scene scene, MeshQuality? fallbackQuality = null,
        Func<Scene, Viewer.Animation?>? animationFactory = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
        _factory = () => scene;
        _fallbackQuality = fallbackQuality;
        _animationFactory = animationFactory;
    }

    /// <summary>
    /// The host's animation over the current scene, or null when the program declared
    /// none (<c>EngrCad.Configure().WithAnimation(...)</c>). Built on first ask rather
    /// than at startup — the laziness rule this whole session follows, since track
    /// construction can mesh for bounds — and discarded by <see cref="Reload"/> so the
    /// timeline follows the model.
    /// </summary>
    public Viewer.Animation? Animation
    {
        get
        {
            lock (_gate)
            {
                if (_animationBuilt)
                    return _animation;
                _animationBuilt = true;
                _animation = _animationFactory?.Invoke(_scene);
                return _animation;
            }
        }
    }

    /// <summary>The current scene. Replaced wholesale by <see cref="Reload"/>, so a
    /// tool should read it once and work with that reference.</summary>
    public Scene Scene
    {
        get { lock (_gate) return _scene; }
    }

    /// <summary>Increments on every successful <see cref="Reload"/> and on every
    /// geometry-changing edit (<see cref="NoteMutation"/>) — the cheap
    /// "is what I saw still current?" token for clients.</summary>
    public int Generation
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>Bumps <see cref="Generation"/> after a tool changed the model in place
    /// (a parameter edit, a suppression toggle) so clients can tell their earlier
    /// reads are stale. Returns the new generation.</summary>
    public int NoteMutation()
    {
        lock (_gate)
            return ++_generation;
    }

    /// <summary>The mesh quality this session displays and exports at: the scene's own
    /// options when it chose them, otherwise the host default.</summary>
    public MeshQuality Quality => Scene.ResolveQuality(_fallbackQuality);

    /// <summary>
    /// Re-invokes the scene factory and swaps the result in — the <c>reload</c> tool,
    /// mirroring what a <c>dotnet watch</c> hot reload does to the viewer. A factory
    /// that throws leaves the last good scene in place (again mirroring the live loop)
    /// and the exception propagates to the caller for reporting.
    /// </summary>
    public Scene Reload()
    {
        var rebuilt = _factory();   // outside the lock: model code can be slow
        lock (_gate)
        {
            _scene = rebuilt;
            _animation = null;          // the timeline belongs to the scene it was built over
            _animationBuilt = false;
            _generation++;
            return rebuilt;
        }
    }

    /// <summary>
    /// Replaces the session's scene with one that did NOT come from the factory — a
    /// document loaded from disk. The factory is untouched, so <see cref="Reload"/>
    /// still re-runs the program's source and discards the loaded document: the
    /// standing rule that <b>the program's source is the truth</b>, with a file as a
    /// session-lifetime overlay. Bumps <see cref="Generation"/>.
    /// </summary>
    public Scene Adopt(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            _scene = scene;
            _animation = null;          // built over the scene it was handed
            _animationBuilt = false;
            _generation++;
            return scene;
        }
    }

    /// <summary>One part's display mesh at this session's quality — the lazy seam.
    /// Meshes cache on the part, so a second tool call is free.</summary>
    public Mesh.HalfEdgeMesh MeshOf(Part part) => part.GetMesh(Quality);

    /// <summary>Meshes the whole scene (idempotent, cached per part). Only the tools
    /// that genuinely need every part call this, and they go through
    /// <see cref="Scene.PreMesh"/> so they inherit whatever that routine learns to do
    /// (parallelism, per-tab laziness) without forking it here.</summary>
    public void PreMesh() => Scene.PreMesh(_fallbackQuality);

    /// <summary>
    /// Meshes exactly the parts these instances reference (each distinct part once) —
    /// what a tab-scoped or part-scoped screenshot/export needs, so narrowing the scope
    /// narrows the work instead of quietly tessellating the whole document.
    /// </summary>
    public void PreMesh(IReadOnlyList<PartInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var quality = Quality;
        foreach (var part in instances.Select(i => i.Part).Distinct())
        {
            part.GetMesh(quality);
            part.GetFeatureEdges(quality);   // the overlay geometry, primed off any render path
        }
    }

    /// <summary>
    /// Finds a part by name. <paramref name="name"/> may be a bare part name or a
    /// "tab/part" path; <paramref name="tab"/> narrows the search to one tab. Returns
    /// null when nothing matches. Part names are unique per tab, so a bare name that
    /// exists in several tabs resolves to the first tab in scene order — pass the tab
    /// (or "tab/part") to disambiguate.
    /// </summary>
    public (Tab Tab, Part Part)? FindPart(string name, string? tab = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var scene = Scene;

        // "tab/part" is accepted verbatim; '/' is reserved for paths everywhere else
        // in the document model, so it cannot appear inside a tab or part name.
        if (tab is null && name.IndexOf('/') is > 0 and var slash)
        {
            tab = name[..slash];
            name = name[(slash + 1)..];
        }

        foreach (var candidate in scene.Tabs)
        {
            if (tab is not null && !string.Equals(candidate.Name, tab, StringComparison.Ordinal))
                continue;
            foreach (var part in candidate.AllParts)
            {
                if (string.Equals(part.Name, name, StringComparison.Ordinal))
                    return (candidate, part);
            }
        }
        return null;
    }

    /// <summary>The tab of the given name, or null.</summary>
    public Tab? FindTab(string name) =>
        Scene.Tabs.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>Which tab shows a part (the first one, if it is shown in several).</summary>
    public Tab? TabOf(Part part) => Scene.Tabs.FirstOrDefault(t => t.AllParts.Contains(part));
}
