using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// The rollback bar's suppression bookkeeping, UI-free so it can be unit-tested
/// (SceneHost owns only the marker buttons). Rolling back to a feature suppresses
/// every feature BELOW it; moving the marker down restores the ones above it; the
/// last feature's marker restores the whole history. <paramref name="rolled"/> records
/// which features THIS mechanism suppressed, so restoring never un-suppresses a
/// feature the user suppressed deliberately — the two suppression sources compose.
/// The caller regenerates afterwards (this only flips <see cref="Feature.Suppressed"/>
/// flags, which is exactly the seam <c>FeatureHistory</c> keys its prefix cache on).
/// </summary>
internal static class FeatureRollback
{
    /// <summary>Moves the rollback marker to <paramref name="marker"/>. Returns true
    /// when any suppression flag changed (the caller should regenerate).</summary>
    public static bool RollBackTo(FeatureHistory history, Feature marker, HashSet<Feature> rolled)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(rolled);
        int at = IndexOf(history, marker);
        if (at < 0)
            return false;
        bool changed = false;
        for (int i = 0; i < history.Features.Count; i++)
        {
            var feature = history.Features[i];
            if (i > at && !feature.Suppressed)
            {
                feature.Suppressed = true;
                rolled.Add(feature);
                changed = true;
            }
            else if (i <= at && feature.Suppressed && rolled.Remove(feature))
            {
                feature.Suppressed = false;
                changed = true;
            }
        }
        return changed;
    }

    private static int IndexOf(FeatureHistory history, Feature marker)
    {
        for (int i = 0; i < history.Features.Count; i++)
        {
            if (ReferenceEquals(history.Features[i], marker))
                return i;
        }
        return -1;
    }
}
