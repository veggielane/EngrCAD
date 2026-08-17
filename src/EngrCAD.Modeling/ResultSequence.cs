using System.Globalization;
using EngrCAD.Mesh;

namespace EngrCAD.Modeling;

/// <summary>
/// A named TIME AXIS over a part's results: the ordered (result name, seconds) steps a
/// transient run published. The states themselves are ordinary <see cref="MeshField"/>
/// results — what was missing was the AXIS: the instants used to live only in the
/// <c>FieldSequenceTrack</c> an application hand-built, so a saved document kept every
/// state and lost the order and the times. A sequence is document data
/// (<see cref="Part.AddResultSequence"/>, persisted write-only-when-stated), and the
/// track is built FROM it by one rule (<c>FieldSequenceTrack.For</c>), so the saved
/// axis and the playback cannot disagree.
/// <para>Validation mirrors the track's own: at least one step, every step naming a
/// result, times finite and strictly increasing — refused here, at the document
/// boundary, rather than discovered when a clip is first played.</para>
/// </summary>
public sealed class ResultSequence
{
    /// <summary>
    /// A sequence over steps already published as results. Most callers want
    /// <see cref="Part.AddResultSequence"/>, which derives the per-step result names and
    /// attaches the fields in the same call.
    /// </summary>
    public ResultSequence(string name, IReadOnlyList<(string ResultName, double Seconds)> steps)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A result sequence needs a non-empty name.", nameof(name));
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
            throw new ArgumentException(
                $"Result sequence '{name}' needs at least one step.", nameof(steps));
        var copy = new (string, double)[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            var (resultName, seconds) = steps[i];
            if (string.IsNullOrEmpty(resultName))
                throw new ArgumentException(
                    $"Result sequence '{name}': step {i} names no result.", nameof(steps));
            if (!double.IsFinite(seconds))
                throw new ArgumentOutOfRangeException(nameof(steps),
                    $"Result sequence '{name}': step {i}'s time is not finite.");
            if (i > 0 && !(seconds > copy[i - 1].Item2))
                throw new ArgumentException(
                    $"Result sequence '{name}': step times must strictly increase " +
                    $"({seconds:G6} after {copy[i - 1].Item2:G6}).", nameof(steps));
            copy[i] = (resultName, seconds);
        }
        Name = name;
        Steps = copy;
    }

    /// <summary>The axis's own name ("Temperature") — what a caller plays by.</summary>
    public string Name { get; }

    /// <summary>The steps: each result NAME with its instant, in time order.</summary>
    public IReadOnlyList<(string ResultName, double Seconds)> Steps { get; }

    /// <summary>
    /// The derived per-step result name — <c>"{name} @ {seconds}s"</c> with the seconds
    /// in round-trip ("R") form, so the name is a deterministic function of the instant
    /// and survives the document file bit-for-bit.
    /// </summary>
    public static string StepName(string name, double seconds) =>
        $"{name} @ {seconds.ToString("R", CultureInfo.InvariantCulture)}s";
}
