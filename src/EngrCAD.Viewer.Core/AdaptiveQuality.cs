using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The camera-adaptive display-quality decision rule — pure state, no GL, no timer —
/// behind the opt-in <c>EngrCadOptions.AdaptiveDisplayQuality</c>. Feed it the camera
/// pose and viewport height on a coarse cadence (<c>SceneHost</c> polls; a test passes
/// fake instants) and it answers with the <see cref="MeshQuality"/> the displayed parts
/// should refine to, or null when nothing should happen — which is almost always.
/// <para><b>The criterion is one line and the plumbing is the feature</b> (the design
/// the backlog entry recorded): the target is a chord deviation of
/// <see cref="PixelFraction"/> of a device pixel <b>at the depth being sized</b> — each
/// part's own depth (<see cref="QualityFor(CameraState, double, in Vector3d)"/>), with the
/// orbit target's serving the scene-level "is this pose worth acting on" decision — fed to
/// the landed
/// <see cref="TessellationQuality.MaxChordDeviation"/> criterion, so a large rim gains
/// segments as the camera closes in on it. Three rules keep it from being a per-frame
/// knob:</para>
/// <list type="bullet">
/// <item><b>Settle, never per frame</b>: a pose is only evaluated after it has been
/// observed unchanged for <see cref="SettleDelay"/> (and each settled pose exactly
/// once), so a drag or a wheel flurry queues nothing.</item>
/// <item><b>Factor-of-two hysteresis</b>: a settled target is adopted only when it is
/// at least <see cref="HysteresisFactor"/> finer than the last adopted one, so small
/// zooms cost nothing.</item>
/// <item><b>Refinement only — the never-coarsen ratchet</b>: a coarser target is never
/// adopted (zooming out re-meshes nothing), and the emitted quality carries the
/// session baseline's own segment count as <see cref="TessellationQuality.MinSegments"/>,
/// so no part can ever resolve below the quality the session started at. A part that
/// visibly loses detail on pull-back reads as a bug even when a criterion is working —
/// the entry's own reading — so the trade is memory (capped by
/// <see cref="RefinementCeiling"/>), not detail. <c>Part.RefineMesh</c> enforces the
/// same ratchet per part, so the guarantee does not rest on this controller's state
/// surviving tab switches.</item>
/// </list>
/// <para>Offscreen renders and exports never consult this type: they are deterministic
/// one-shots at the stated quality, and a headless PNG whose resolution depended on a
/// camera distance would make the committed docs images a function of framing.</para>
/// </summary>
public sealed class AdaptiveQuality
{
    /// <summary>How long the camera must hold one pose before it is evaluated. A
    /// re-mesh mid-gesture is wasted work superseded by the next wheel notch, so the
    /// rule fires only when the user has stopped moving.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>A settled target is adopted only when at least this factor finer than
    /// the last adopted one — the re-mesh-on-a-factor-of-two rule that keeps a slow
    /// zoom from queueing a tessellation per notch.</summary>
    public const double HysteresisFactor = 2.0;

    /// <summary>The chord deviation asked for, as a fraction of one device pixel at
    /// the orbit target — half a pixel, below which extra segments move nothing a
    /// viewer can see.</summary>
    public const double PixelFraction = 0.5;

    /// <summary>Upper clamp on refined segments per circle (the emitted quality's
    /// <see cref="TessellationQuality.MaxSegments"/> when the session states no
    /// criterion of its own) — the memory bound the never-coarsen ratchet trades
    /// against.</summary>
    public const int RefinementCeiling = 512;

    private readonly MeshQuality? _baseline;
    private CameraState? _pose;
    private double _viewportHeight;
    private TimeSpan _poseSince;
    private bool _evaluated;
    private double _adopted = double.PositiveInfinity;

    /// <param name="baseline">The session's resolved display quality
    /// (<c>Scene.ResolveQuality</c>'s answer) — the floor the ratchet protects. Null
    /// means <see cref="MeshQuality"/>'s own defaults.</param>
    public AdaptiveQuality(MeshQuality? baseline) => _baseline = baseline;

    /// <summary>
    /// The chord deviation one settled pose asks for: <see cref="PixelFraction"/> of
    /// the world size of a device pixel at the orbit target,
    /// <c>2·distance·tan(fov/2)/height</c> under the shared <see cref="CameraMath.FovY"/>
    /// (the orthographic toggle sizes its frustum to keep the target plane's apparent
    /// size, so the same figure serves both projections).
    /// </summary>
    public static double ChordDeviationFor(CameraState camera, double viewportHeightPixels)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (viewportHeightPixels <= 0 || !double.IsFinite(viewportHeightPixels))
            throw new ArgumentOutOfRangeException(nameof(viewportHeightPixels),
                viewportHeightPixels, "Viewport height must be a positive pixel count.");
        return PixelFraction * 2 * camera.Distance * Math.Tan(CameraMath.FovY / 2) / viewportHeightPixels;
    }

    /// <summary>
    /// The chord deviation ONE PART asks for: the same rule measured at that part's own
    /// depth rather than at the orbit target's. A pixel's world size grows linearly with
    /// depth, so in a wide scene a part twice as far as the target needs half the segments
    /// and one half as far needs √2 times as many — sizing every part by the target's
    /// distance over-refines the far ones and under-refines the near ones by exactly that
    /// factor.
    /// <para><paramref name="worldPoint"/> is the part's bounds CENTRE (the target is a
    /// centre-like point, so this is the direct generalisation). Depth is measured along
    /// the view direction, and a part at or behind the eye is clamped to
    /// <see cref="MinimumDepthFraction"/> of the camera distance: such a part is mostly
    /// off screen, and the clamp keeps the answer positive and finite without letting one
    /// stray centre demand an unbounded refinement — <see cref="RefinementCeiling"/>
    /// bounds it in any case.</para>
    /// </summary>
    public static double ChordDeviationFor(
        CameraState camera, double viewportHeightPixels, in Vector3d worldPoint)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (viewportHeightPixels <= 0 || !double.IsFinite(viewportHeightPixels))
            throw new ArgumentOutOfRangeException(nameof(viewportHeightPixels),
                viewportHeightPixels, "Viewport height must be a positive pixel count.");

        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var toTarget = camera.Target - eye;
        double span = toTarget.Length;
        if (!(span > 0) || !double.IsFinite(span))
            return ChordDeviationFor(camera, viewportHeightPixels);

        double depth = (worldPoint - eye).Dot(toTarget / span);
        double floor = MinimumDepthFraction * camera.Distance;
        if (!double.IsFinite(depth) || depth < floor)
            depth = floor;
        return PixelFraction * 2 * depth * Math.Tan(CameraMath.FovY / 2) / viewportHeightPixels;
    }

    /// <summary>Depth floor for <see cref="ChordDeviationFor(CameraState, double, in Vector3d)"/>,
    /// as a fraction of the camera distance — what a part whose centre sits at or behind
    /// the eye is sized by.</summary>
    public const double MinimumDepthFraction = 1.0 / 64;

    /// <summary>The quality ONE PART should refine to under a settled pose — the per-part
    /// twin of <see cref="QualityFor(double)"/>, sized at that part's own depth. The host
    /// calls this once per displayed part after <see cref="Observe"/> has decided that the
    /// pose is worth acting on at all; the never-coarsen ratchet is enforced per part by
    /// <c>Part.RefineMesh</c>, so a part that ends up asking for less than it already has
    /// simply re-meshes nothing.</summary>
    public MeshQuality QualityFor(
        CameraState camera, double viewportHeightPixels, in Vector3d worldPoint) =>
        QualityFor(ChordDeviationFor(camera, viewportHeightPixels, worldPoint));

    /// <summary>
    /// One observation of the camera. Returns the quality the displayed parts should
    /// refine to — only when the pose has settled (<see cref="SettleDelay"/> unchanged),
    /// has not been evaluated before, and its target clears the hysteresis and the
    /// ratchet — otherwise null. <paramref name="elapsed"/> is any monotonic clock
    /// (the host's stopwatch; a test's fake instants).
    /// </summary>
    public MeshQuality? Observe(CameraState camera, double viewportHeightPixels, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (viewportHeightPixels <= 0 || !double.IsFinite(viewportHeightPixels))
            return null;   // no viewport yet — nothing to size against

        // Exact comparison on purpose (the scale-free exact-zero SEMANTIC tier): the
        // question is "did the pose change at all since the last look", and any change,
        // however small, restarts the settle clock. A resize counts as a move.
        if (_pose is null || camera != _pose || viewportHeightPixels != _viewportHeight)
        {
            _pose = camera;
            _viewportHeight = viewportHeightPixels;
            _poseSince = elapsed;
            _evaluated = false;
            return null;
        }
        if (_evaluated || elapsed - _poseSince < SettleDelay)
            return null;

        _evaluated = true;   // each settled pose is evaluated exactly once
        double target = ChordDeviationFor(camera, viewportHeightPixels);
        // Smaller deviation = finer. Adopt only a target at least HysteresisFactor
        // finer than the incumbent; a coarser target (zooming out) never adopts.
        if (!(target < _adopted / HysteresisFactor))
            return null;
        _adopted = target;
        return QualityFor(target);
    }

    /// <summary>
    /// The quality a given chord deviation asks the display path for: the session
    /// baseline with an adaptive <see cref="TessellationQuality"/> whose
    /// <see cref="TessellationQuality.MinSegments"/> is the baseline's own segment
    /// count — the floor that makes "never coarser than the session started" structural
    /// per part. A baseline that already states its own criterion keeps its angle
    /// criterion, clamps and the FINER of the two deviations, so the adaptive layer can
    /// only ever refine what the session asked for.
    /// </summary>
    public MeshQuality QualityFor(double chordDeviation)
    {
        if (!double.IsFinite(chordDeviation) || chordDeviation <= 0)
            throw new ArgumentOutOfRangeException(nameof(chordDeviation), chordDeviation,
                "A chord deviation must be positive.");

        var quality = new MeshQuality();
        if (_baseline is not null)
        {
            quality.SegmentsPerCircle = _baseline.SegmentsPerCircle;
            quality.CurveSamples = _baseline.CurveSamples;
            quality.SdfResolution = _baseline.SdfResolution;
            quality.SurfaceNets = _baseline.SurfaceNets;
        }
        quality.Tessellation = _baseline?.Tessellation is { } session
            ? new TessellationQuality
            {
                MaxAngleDegrees = session.MaxAngleDegrees,
                MaxChordDeviation = session.MaxChordDeviation is { } stated
                    ? Math.Min(stated, chordDeviation)
                    : chordDeviation,
                MinSegments = session.MinSegments,
                MaxSegments = session.MaxSegments,
            }
            : new TessellationQuality
            {
                MaxChordDeviation = chordDeviation,
                MinSegments = Math.Max(3, quality.SegmentsPerCircle),
                MaxSegments = Math.Max(Math.Max(3, quality.SegmentsPerCircle), RefinementCeiling),
            };
        return quality;
    }
}
