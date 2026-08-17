using System.Runtime.CompilerServices;
using EngrCAD.Interop;

namespace EngrCAD.Modeling;

/// <summary>
/// A model whose GEOMETRY is a function of time — OpenSCAD's <c>$t</c>: a spring
/// compressing, a bellows folding, a parametric sweep played as a clip. It is the
/// expensive cousin of the <c>Animation</c> timeline and deliberately a different type,
/// because an animation's load-bearing rule is that it must not touch geometry: a track
/// answers with matrices, a camera or a scalar, which is what lets a whole clip animate
/// with buffers already uploaded. A model that MORPHS breaks that rule by definition, so
/// it is not a track — it is a <see cref="Func{Double, Scene}"/> the caller BAKES, one
/// full lower + tessellate per frame.
/// <para><b>The cost is the whole design constraint, so it is stated rather than
/// implied.</b> A frame costs whatever <c>Scene.PreMesh</c> costs — measured (win-x64) at
/// <b>8.5 ms a frame uncached and 4.2 ms cached</b> on the mesh-route docs fixture, and
/// <b>20–45 ms</b> for one instant of a B-Rep model (a boolean bore, a whole-solid round),
/// geometry alone with no render. That is one to three times a 60 Hz frame budget for a
/// small model and grows with the part, so it is a bake rather than a scrub: the
/// interactive transport's contract is "pure function, instant" and a morphing model
/// cannot honour it, so live scrubbing is refused by name (see the remarks on
/// <see cref="At"/> for the live recipe that does work).</para>
/// <para><b>What this type adds over calling the factory yourself is the CACHE</b>, and
/// the cache is the reason a bake is affordable at all: across frames most of a model is
/// unchanged, and a sub-graph the factory returns unchanged is the SAME OBJECT. So a
/// part whose geometry object has already been meshed at this quality adopts that part's
/// derived caches — the display mesh, the B-Rep and implicit lowerings, the feature-edge
/// overlay — instead of recomputing them. Those caches are pure functions of (geometry,
/// quality) and the geometry is literally the same object, so the transplanted result is
/// not merely equal to what the frame would have computed, it IS that object: the cache
/// cannot change the answer, and a test pins cached and uncached bakes byte-identical.
/// </para>
/// <para><b>Hoisting is what makes it hit.</b> A factory that rebuilds its whole graph
/// every frame shares no object and hits nothing — honestly reported rather than
/// papered over. Hoist the parts that do not depend on t into a field or a captured
/// local and they cache; see <see cref="ModelCacheReport"/>.</para>
/// <para>The second mechanism needs nothing here: a <see cref="FeatureHistory"/>-backed
/// part driven by <c>SetParameter</c> + <c>Regenerate</c> already reuses the unchanged
/// PREFIX of its history through the regeneration cache, and reports it through
/// <c>RegenerationResult</c>. That is a different saving at a different granularity and
/// the two compose; see <c>docs/examples/animation.md</c> for both measured.</para>
/// </summary>
public sealed class TimeVaryingModel
{
    private readonly Func<double, Scene> _factory;
    private readonly MeshQuality? _quality;
    private readonly bool _caching;
    private readonly Dictionary<CacheKey, Part> _cache = [];
    private int _frames;
    private int _built;
    private int _reused;

    /// <summary>Internal seam: drops the mesh quality from the cache key, which is the
    /// one thing that makes a transplant unsound (a mesh built at one resolution
    /// standing in for another). Flipped only by the test that SHOWS the defect the key
    /// prevents — the repo's shown-to-fire rule for a guard with no compiler behind
    /// it.</summary>
    internal bool KeyOnQuality { get; init; } = true;

    /// <summary>Internal seam: how many geometries the cache is holding meshes for. The
    /// eviction rule's own claim — that a morphing part's per-frame geometry cannot
    /// accumulate — is a statement about this number, so a test reads it rather than
    /// taking the prose on trust.</summary>
    internal int CachedEntryCount => _cache.Count;

    /// <param name="factory">Builds the scene at timeline fraction t. Called once per
    /// frame; it must be a pure function of t (the bake asserts determinism).</param>
    /// <param name="quality">Host-level fallback quality, used only where the scene did
    /// not choose its own (<c>Scene.ResolveQuality</c>'s precedence).</param>
    /// <param name="cache">False disables the geometry cache — the honest baseline every
    /// speed claim here is measured against, and the arm a byte-identity test compares
    /// the cached bake with.</param>
    public TimeVaryingModel(Func<double, Scene> factory, MeshQuality? quality = null, bool cache = true)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _quality = quality;
        _caching = cache;
    }

    /// <summary>What the cache did over every <see cref="At"/> call so far.</summary>
    public ModelCacheReport Cache => new(_frames, _built, _reused);

    /// <summary>
    /// The scene at timeline fraction <paramref name="t"/>, fully prepared: the factory
    /// is invoked, every part whose geometry this model has already meshed at this
    /// quality adopts those caches, and <c>Scene.PreMesh</c> builds whatever is left —
    /// so the returned scene is ready to render with no lowering on a render thread.
    /// <para><b>Live scrubbing is refused by name rather than offered</b>: the viewer's
    /// animation transport drives <c>Animation.At</c>, a pure matrices-only function it
    /// scrubs at frame rate, and a call to THIS method costs a full lower + tessellate.
    /// The live recipe that does work is the hot-reload loop, which is already this
    /// pipeline for one frame — hold the model and the current t in the host, pass
    /// <c>() =&gt; model.At(t)</c> to <c>EngrCad.ShowLive</c>, and call
    /// <c>EngrCad.NotifySourceChanged()</c> when t moves. That path keeps the camera,
    /// keeps the last good scene if the factory throws, and reuses this cache; what it
    /// does not do is pretend to be instant.</para>
    /// </summary>
    public Scene At(double t)
    {
        var scene = _factory(t)
            ?? throw new InvalidOperationException(
                $"The model factory returned null at t = {t:G6}; a time-varying model must "
                + "produce a Scene at every instant.");

        var quality = scene.ResolveQuality(_quality);
        var signature = KeyOnQuality ? QualitySignature.Of(quality) : default;
        var parts = scene.AllParts.ToArray();

        if (_caching)
        {
            foreach (var part in parts)
            {
                // Already meshed: the factory handed back a Part object this bake has
                // seen (the hoist-the-Part spelling), so there is nothing to transplant
                // and nothing to build.
                if (part.HasMesh)
                {
                    _reused++;
                    continue;
                }
                // A source with no mesh would transplant an empty state — a wasted miss
                // that the accounting would report as a hit, so it is excluded here and
                // the honest answer is "built".
                if (_cache.TryGetValue(new CacheKey(part.Geometry, signature), out var source)
                    && !ReferenceEquals(source, part)
                    && source.HasMesh)
                {
                    part.AdoptDerivedFrom(source);
                    _reused++;
                    continue;
                }
                _built++;
            }
        }
        else
        {
            _built += parts.Length;
        }

        scene.PreMesh(_quality);

        if (_caching)
        {
            // Retain exactly this frame's geometries. A hoisted (t-independent) sub-graph
            // is present in every frame, so it survives; a morphing part's geometry is a
            // fresh object each frame and its predecessor is dropped, which bounds the
            // cache at one frame's worth of meshes rather than the whole clip's. The
            // frames a bake retains for its own framing dominate either way.
            var live = new Dictionary<CacheKey, Part>(parts.Length);
            foreach (var part in parts)
            {
                var key = new CacheKey(part.Geometry, signature);
                if (!live.ContainsKey(key))
                    live[key] = _cache.TryGetValue(key, out var existing) && existing.HasMesh ? existing : part;
            }
            _cache.Clear();
            foreach (var (key, part) in live)
                _cache[key] = part;
        }

        _frames++;
        return scene;
    }

    /// <summary>Releases every cached mesh and lowering, and resets the report. The
    /// answer <see cref="At"/> gives is unchanged — only the work it does.</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _frames = 0;
        _built = 0;
        _reused = 0;
    }

    /// <summary>The geometry OBJECT (reference) plus the quality it was meshed at. The
    /// quality half is what stops a mesh built for one resolution standing in for
    /// another; the geometry half is reference identity on purpose, because two
    /// structurally equal graphs are not the same cached mesh and proving them equal
    /// would cost more than rebuilding.</summary>
    private readonly struct CacheKey(object geometry, QualitySignature quality) : IEquatable<CacheKey>
    {
        private readonly object _geometry = geometry;
        private readonly QualitySignature _quality = quality;

        public bool Equals(CacheKey other) =>
            ReferenceEquals(_geometry, other._geometry) && _quality.Equals(other._quality);

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(_geometry), _quality);
    }

    /// <summary>A <see cref="MeshQuality"/> by VALUE — it is a mutable class, so its own
    /// reference says nothing about whether two scenes ask for the same discretization.
    /// The two nested option objects compare by reference, which errs toward a cache MISS
    /// (a rebuild) rather than toward a wrong transplant.</summary>
    private readonly record struct QualitySignature(
        int SegmentsPerCircle, int CurveSamples, int SdfResolution,
        TessellationQuality? Tessellation, SurfaceNetsOptions? SurfaceNets)
    {
        public static QualitySignature Of(MeshQuality quality) => new(
            quality.SegmentsPerCircle, quality.CurveSamples, quality.SdfResolution,
            quality.Tessellation, quality.SurfaceNets);
    }
}

/// <summary>
/// What a <see cref="TimeVaryingModel"/>'s cache did: how many part-meshings a bake
/// paid for and how many it inherited from an earlier frame. <see cref="HitRate"/> is
/// the number to read — a factory that hoists its t-independent geometry approaches
/// <c>1 − (changing parts)/(all parts)</c>, one that rebuilds everything reads 0.
/// </summary>
/// <param name="Frames">Frames evaluated.</param>
/// <param name="Built">Parts whose mesh and lowerings this bake computed.</param>
/// <param name="Reused">Parts that took an earlier frame's caches instead.</param>
public readonly record struct ModelCacheReport(int Frames, int Built, int Reused)
{
    /// <summary>Parts visited over every frame (built plus reused).</summary>
    public int Parts => Built + Reused;

    /// <summary>Fraction of part-visits that cost no meshing; 0 when nothing was visited.</summary>
    public double HitRate => Parts == 0 ? 0 : Reused / (double)Parts;

    /// <summary>One line for a log or a docs page.</summary>
    public override string ToString() =>
        $"{Frames} frame(s): {Built} built, {Reused} reused ({HitRate:P0} hit rate)";
}
