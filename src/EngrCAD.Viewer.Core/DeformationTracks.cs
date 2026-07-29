namespace EngrCAD.Viewer;

// Concrete DeformationTracks: the laws that drive a displayed result's exaggeration.
//
// Each one is a function of t returning a MULTIPLIER on every part's own
// FieldDisplay.DeformScale, which reaches the shaders as a single uDeformScale uniform.
// Nothing here touches geometry, so a whole clip reuses one upload — see
// DeformationTrack for why that is the design's central claim rather than an
// optimization.
//
// The honesty rules are in the doc comments rather than in a README because they are
// about what the FRAMES MEAN, and someone reaching for a load ramp needs to read them at
// the call site:
//
//  * A LINEAR result scales exactly, so a ramp's intermediate frames ARE the answers for
//    the intermediate loads. That is what separates this from a cosmetic tween, and it
//    is false for a non-linear solve.
//  * A MODE SHAPE has no physical amplitude at all. Oscillate's amplitude is a display
//    choice; the physics is the shape and its frequency.

/// <summary>
/// The standard deformation laws, and the escape hatch. Every one returns a
/// <see cref="DeformationTrack"/> ready to hand to <c>Animation.With</c>.
/// </summary>
public static class DeformationTracks
{
    /// <summary>
    /// A straight ramp from <paramref name="from"/> to <paramref name="to"/> — the
    /// simplest law, and the one to reach for when the clip should end deformed
    /// (0 → 1 leaves the result standing at its stated exaggeration).
    /// </summary>
    public static DeformationTrack Ramp(double from = 0, double to = 1) =>
        From(t => from + (to - from) * t);

    /// <summary>
    /// A load ramp: <c>0 → peak → 0</c>, the load applied and released. The obvious first
    /// demo of an animated structural result, and the honest one for a <b>linear</b>
    /// solve — a linear result scales exactly with the load, so the frame at factor 0.5
    /// is the actual answer for half the load rather than an interpolation between two
    /// computed states.
    /// <para>Say so when the solve is NOT linear (contact, plasticity, large
    /// deflection): the shape still scales on screen, but only the endpoint was solved
    /// and the intermediate frames are then illustration.</para>
    /// <para>The peak is reached exactly at t = 0.5 and both ends are exactly 0, so the
    /// clip loops seamlessly under <c>AnimationEasing.Linear</c>.</para>
    /// </summary>
    /// <param name="peak">Factor at mid-timeline (1 = each part's own stated
    /// exaggeration).</param>
    public static DeformationTrack LoadRamp(double peak = 1) =>
        From(t => peak * (1 - Math.Abs(2 * t - 1)));

    /// <summary>
    /// A sinusoid through <c>+amplitude</c> and <c>−amplitude</c> —
    /// <c>amplitude * sin(2*pi*cycles*t)</c>, so it starts and ends at exactly 0 for a
    /// whole number of cycles and loops seamlessly.
    /// <para><b>This is the mode-shape animation</b>, and it needs nothing from the
    /// solver beyond the mode published as an ordinary vector result: vibrating in a
    /// mode IS the shape scaled by <c>cos(omega*t)</c>, which is why the same track
    /// serves a swaying beam and a ringing bracket. Use a small
    /// <paramref name="cycles"/> — 2 or 3 — and state the slowdown.</para>
    /// <para><b>Do NOT set cycles = frequency x Duration.</b> It is dimensionally right
    /// and useless: a steel blade 80 mm long and 6 mm thick rings near 780 Hz, so a
    /// two-second clip at true speed asks for ~1570 cycles, hundreds per rendered frame,
    /// which aliases into noise or a stationary blur — and no frame rate fixes it,
    /// because the mode is faster than video. Every stiff metal part is like this
    /// (hundreds of hertz to tens of kilohertz); the structures slow enough to animate at
    /// true speed are things like tall buildings. So a mode animates at a CHOSEN playback
    /// rate, and the honest caption says by what factor it is slowed.</para>
    /// <para><b>Caveat to repeat wherever this is used</b>: a mode shape has no physical
    /// amplitude, and its sign is a convention. The animation's amplitude is a display
    /// choice; what is physical is the shape and the frequency. And a mode's DIRECTION is
    /// well defined only when its frequency is simple — on a symmetric part the two
    /// bending modes are degenerate, so what is animated is one valid member of a family
    /// rather than the mode.</para>
    /// </summary>
    /// <param name="amplitude">Peak factor, in both directions.</param>
    /// <param name="cycles">Whole or fractional periods across the timeline.</param>
    public static DeformationTrack Oscillate(double amplitude = 1, double cycles = 1) =>
        From(t => amplitude * Math.Sin(2 * Math.PI * cycles * t));

    /// <summary>Holds one constant factor for the whole clip — a still deformation under
    /// a camera track (an orbit around a deflected shape).</summary>
    public static DeformationTrack Constant(double factor) => From(_ => factor);

    /// <summary>
    /// Any law of track-local t ∈ [0,1]. The escape hatch, and where a sequenced
    /// deformation lives — an animation takes at most one deformation track, so a
    /// hold-then-release is written as one function rather than as two tracks with no
    /// defined composition.
    /// </summary>
    public static DeformationTrack From(Func<double, double> law) => new FunctionTrack(law);

    private sealed class FunctionTrack(Func<double, double> law) : DeformationTrack
    {
        private readonly Func<double, double> _law =
            law ?? throw new ArgumentNullException(nameof(law));

        public override double ScaleAt(double t) => _law(t);
    }
}
