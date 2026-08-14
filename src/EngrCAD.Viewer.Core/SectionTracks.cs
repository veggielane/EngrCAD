using EngrCAD.Core;

namespace EngrCAD.Viewer;

/// <summary>
/// A <see cref="SectionTrack"/> sweeping ONE clip plane from a start offset to an end offset
/// along a fixed normal — the material-addition / material-removal player. The renderer keeps
/// everything with <c>dot(world, Normal) ≤ offset</c>, so sweeping the offset UP a part's build
/// direction replays the part being printed (each instant shows exactly the material below the
/// plane — for planar slicing that IS the printed state, no re-meshing anywhere), and sweeping
/// it down replays material being removed.
///
/// <para><see cref="Steps"/> quantizes the sweep: with N steps the offset jumps in N equal
/// increments (ceiling, so t = 0 shows the start state and any t &gt; 0 has completed whole
/// steps) — set it to a slice's LAYER COUNT and the reveal steps layer by layer, which is what
/// a print does; 0 sweeps smoothly.</para>
/// </summary>
public sealed class SweepSectionTrack : SectionTrack
{
    private readonly Vector3d _normal;
    private readonly double _from;
    private readonly double _to;

    /// <summary>The number of equal steps the sweep quantizes to (0 = smooth).</summary>
    public int Steps { get; }

    internal SweepSectionTrack(in Vector3d normal, double from, double to, int steps)
    {
        if (!(normal.Length > 0) || !double.IsFinite(normal.Length))
            throw new ArgumentException("A section sweep needs a nonzero finite normal.", nameof(normal));
        if (!double.IsFinite(from) || !double.IsFinite(to))
            throw new ArgumentException("A section sweep needs finite offsets.");
        if (steps < 0)
            throw new ArgumentException($"Steps must be non-negative; got {steps}.", nameof(steps));
        _normal = normal.Normalized();
        _from = from;
        _to = to;
        Steps = steps;
    }

    /// <summary>The plane's offset at track-local <paramref name="t"/>.</summary>
    public double OffsetAt(double t)
    {
        double fraction = Steps > 0 ? Math.Ceiling(t * Steps) / Steps : t;
        return _from + (_to - _from) * Math.Clamp(fraction, 0, 1);
    }

    /// <inheritdoc/>
    public override IReadOnlyList<SectionPlane> SectionsAt(double t) =>
        [new SectionPlane(_normal, OffsetAt(t))];
}

/// <summary>The section-track laws.</summary>
public static class SectionTracks
{
    /// <summary>A single clip plane swept from <paramref name="fromOffset"/> to
    /// <paramref name="toOffset"/> along <paramref name="normal"/>; see
    /// <see cref="SweepSectionTrack"/> for the step quantization.</summary>
    public static SweepSectionTrack Sweep(
        in Vector3d normal, double fromOffset, double toOffset, int steps = 0) =>
        new(normal, fromOffset, toOffset, steps);

    /// <summary>
    /// The PRINT-PROGRESS reveal over a body's bounds: at t = 0 nothing is visible, at t = 1
    /// everything is — the clip plane rises along <paramref name="growDirection"/> from just
    /// below the bounds to just above them. Pass a slice's layer count as
    /// <paramref name="steps"/> and the reveal completes whole layers, exactly as a printer
    /// does; for planar slicing each instant IS the printed state (the material below the
    /// plane), so the animation needs no re-meshing and honours the timeline rule by
    /// construction.
    /// </summary>
    public static SweepSectionTrack Reveal(in Aabb bounds, in Vector3d growDirection, int steps = 0)
    {
        if (!(growDirection.Length > 0))
            throw new ArgumentException(
                "A reveal needs a nonzero grow direction.", nameof(growDirection));
        var n = growDirection.Normalized();
        // The bounds' extent along the direction, from its eight corners.
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            double d = corner.Dot(n);
            min = Math.Min(min, d);
            max = Math.Max(max, d);
        }
        // A 1% pad each side so t = 0 hides the whole body (the plane strictly below it) and
        // t = 1 shows it whole (strictly above), independent of clip-shader rounding.
        double pad = Math.Max(1e-6, 0.01 * (max - min));
        return new SweepSectionTrack(n, min - pad, max + pad, steps);
    }
}
