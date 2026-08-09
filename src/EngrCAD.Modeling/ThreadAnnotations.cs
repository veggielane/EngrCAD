using EngrCAD.BRep;
using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>One modelled thread a part carries, as a drawing would call it out.</summary>
/// <param name="Spec">The thread's own specification.</param>
/// <param name="Callout">The callout text (<see cref="ThreadCallout"/>).</param>
/// <param name="Anchor">A part-local point ON the thread's crest, where a leader lands.</param>
/// <param name="Axis">The thread's axis, pointing the way the thread advances.</param>
/// <param name="External">True for a stud or a rod, false for a threaded hole.</param>
public sealed record ThreadCalloutSite(
    ThreadSpec Spec, string Callout, Vector3d Anchor, Ray3d Axis, bool External);

/// <summary>
/// The cosmetic-thread half of thread annotation: a part that carries modelled threads
/// labels them with the designation a drawing would carry, rather than leaving a reader
/// to measure a helix.
///
/// <para><b>The spec comes from the construction graph and the ANCHOR from the geometry</b>,
/// and the two are matched by MEASUREMENT rather than by index — a thread's own major
/// radius and pitch, read off the <see cref="HelicalSurface"/> bands the lowering
/// produced. Pairing the n-th graph node with the n-th group of helical faces would be
/// the kind of positional guess a naming scheme must not make: a boolean can reorder
/// faces, a pattern can multiply them, and a wrong pairing puts a correct-looking M6
/// callout on an M10 thread. Matching on the geometry cannot do that; what it can do is
/// find nothing, and then nothing is attached.</para>
///
/// <para><b>Threads are grouped by AXIS, pitch and major radius</b>, so a bolt circle of
/// six identical tapped holes on one axis line is one site per hole and a single stud is
/// one site — the same "N&#xD7;" convention <see cref="HoleAnnotations"/> uses is left to
/// <see cref="HoleTable"/>, which already tables threaded holes by call. What this adds is
/// the EXTERNAL thread, which no hole table can carry because it is not a hole.</para>
/// </summary>
public static class ThreadAnnotations
{
    /// <summary>
    /// The thread callout sites of a part whose geometry is a <see cref="Shape"/> (a
    /// feature-history part's regenerated body is one). Parts with raw B-Rep, mesh or SDF
    /// geometry carry no thread specification and yield an empty list, as do parts whose
    /// B-Rep lowering fails — a callout needs both halves.
    /// </summary>
    public static IReadOnlyList<ThreadCalloutSite> Sites(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Geometry is not Shape shape)
            return [];
        var solid = part.TryGetSolid();
        return solid is null ? [] : Sites(shape, solid);
    }

    /// <summary>
    /// The sites of a shape against a solid already lowered from it — the seam a caller
    /// with both halves in hand uses, and what <see cref="Sites(Part)"/> is written on.
    /// </summary>
    public static IReadOnlyList<ThreadCalloutSite> Sites(Shape shape, BrepSolid solid)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(solid);

        // What the graph knows: which threads were asked for, and (for a hole) how deep.
        var declared = new List<(ThreadSpec Spec, double? Depth, bool External)>();
        foreach (var node in ConstructionTree.FromShape(shape).Flatten())
        {
            switch (node.Target)
            {
                case ThreadShape thread:
                    declared.Add((thread.Spec, null, true));
                    break;
                case ThreadedHoleShape hole:
                    declared.Add((hole.Spec, hole.Depth, false));
                    break;
            }
        }
        if (declared.Count == 0)
            return [];

        var sites = new List<ThreadCalloutSite>();
        foreach (var band in CrestBands(solid))
        {
            // Match on the two numbers a thread IS: its major diameter and its pitch. The
            // weld tier is the right one — both come from the same spec that built the
            // band, so they agree exactly or they are different threads.
            double major = 2 * band.ProfileStart.X;
            double pitch = Math.Abs(band.Pitch);
            var match = declared.FirstOrDefault(d =>
                Math.Abs(d.Spec.MajorDiameter - major) <= Tolerance.Default.Linear &&
                Math.Abs(d.Spec.Pitch - pitch) <= Tolerance.Default.Linear);
            if (match.Spec is null)
                continue;
            double midU = (band.DomainU.Start + band.DomainU.End) / 2;
            var anchor = band.PointAt(midU, 0.5);
            sites.Add(new ThreadCalloutSite(
                match.Spec,
                ThreadCallout.Text(match.Spec, match.External ? null : match.Depth),
                anchor,
                new Ray3d(band.Frame.Origin, band.Frame.Z),
                match.External));
        }
        return sites;
    }

    /// <summary>
    /// Attaches one <see cref="LeaderNote"/> per thread callout site and returns how many.
    /// Idempotent it is not — call it once, as <see cref="HoleAnnotations.AutoAttach"/> is
    /// called once.
    /// </summary>
    public static int AutoAttach(Part part)
    {
        var sites = Sites(part);
        foreach (var site in sites)
            part.Annotate(new LeaderNote(site.Anchor, site.Callout));
        return sites.Count;
    }

    /// <summary>
    /// One CREST band per distinct thread: the helical bands whose generator has a
    /// constant radius (the crest flat, <c>dr == 0</c>) and that radius is the largest any
    /// band of that axis carries, deduplicated by (axis, pitch, radius).
    ///
    /// <para>The constant-radius test is exact rather than tolerant on purpose: a thread's
    /// crest flat is built from two profile corners at the SAME stored radius, so the
    /// difference is bit-zero, while every flank has a real radial run. The largest-radius
    /// rule then separates the crest from the root flat, which is also constant-radius.</para>
    /// </summary>
    private static IEnumerable<HelicalSurface> CrestBands(BrepSolid solid)
    {
        var bands = solid.Faces
            .Select(f => f.Surface)
            .OfType<HelicalSurface>()
            .Where(h => h.ProfileEnd.X == h.ProfileStart.X)
            .ToList();
        var seen = new List<HelicalSurface>();
        foreach (var band in bands)
        {
            // The same thread's root flat is coaxial with its crest at a smaller radius.
            bool outermost = !bands.Any(other =>
                Coaxial(other, band) &&
                Math.Abs(Math.Abs(other.Pitch) - Math.Abs(band.Pitch)) <= Tolerance.Default.Linear &&
                other.ProfileStart.X > band.ProfileStart.X + Tolerance.Default.Linear);
            if (!outermost)
                continue;
            if (seen.Any(s => Coaxial(s, band) &&
                    Math.Abs(s.ProfileStart.X - band.ProfileStart.X) <= Tolerance.Default.Linear &&
                    Math.Abs(Math.Abs(s.Pitch) - Math.Abs(band.Pitch)) <= Tolerance.Default.Linear))
                continue;
            seen.Add(band);
            yield return band;
        }
    }

    private static bool Coaxial(HelicalSurface a, HelicalSurface b)
    {
        if (!a.Frame.Z.IsParallelTo(b.Frame.Z, Tolerance.Default))
            return false;
        var d = a.Frame.Origin - b.Frame.Origin;
        return (d - b.Frame.Z * d.Dot(b.Frame.Z)).Length <= Tolerance.Default.Linear;
    }
}
