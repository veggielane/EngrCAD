using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// Colour maps for field display, as small lookup tables with linear interpolation
// between the stops. Deliberately tables and not formulas: a perceptual map is
// measured, not derived, and a polynomial fit would be a second approximation on top of
// the sampling one with nothing to check it against.
//
// Here rather than in EngrCAD.Modeling for the same reason RenderModes.Resolve is here:
// FieldColorMap is a document-model CHOICE (a design says "viridis" with no viewer
// reference) and this is the one implementation of what that choice looks like. Every
// front end -- window, offscreen, browser -- samples these, so a field cannot be
// coloured two ways.
//
// No dependency is taken for this. The tables are transcribed samples, four lines of
// interpolation, and that is the whole of it.

/// <summary>
/// The colour tables behind <see cref="FieldColorMap"/>: a normalized position in
/// [0, 1] becomes an RGB colour.
/// </summary>
public static class ColorMaps
{
    // Viridis, sampled at 1/16 steps. A piecewise-linear approximation of matplotlib's
    // 256-entry table rather than the table itself: at 17 stops the interpolation error
    // is a couple of percent of a channel, which is below what a shaded fill under a
    // directional light can show, and the point of viridis -- monotone lightness, so it
    // reads in greyscale and under colour-vision deficiency -- survives sampling.
    private static readonly float[] ViridisStops =
    [
        0.267f, 0.005f, 0.329f,
        0.283f, 0.100f, 0.422f,
        0.277f, 0.185f, 0.490f,
        0.254f, 0.265f, 0.530f,
        0.230f, 0.322f, 0.546f,
        0.207f, 0.372f, 0.553f,
        0.185f, 0.418f, 0.556f,
        0.164f, 0.471f, 0.558f,
        0.145f, 0.519f, 0.557f,
        0.129f, 0.567f, 0.551f,
        0.119f, 0.607f, 0.540f,
        0.135f, 0.659f, 0.518f,
        0.208f, 0.719f, 0.473f,
        0.328f, 0.773f, 0.407f,
        0.478f, 0.821f, 0.318f,
        0.648f, 0.858f, 0.210f,
        0.993f, 0.906f, 0.144f,
    ];

    // Moreland's cool-to-warm diverging map (ParaView's default for signed data),
    // sampled at 1/8 steps. The midpoint is a light neutral grey ON PURPOSE: a
    // diverging map's job is to make the crossing visible, and a saturated middle
    // would compete with both ends.
    private static readonly float[] DivergingStops =
    [
        0.230f, 0.299f, 0.754f,
        0.394f, 0.478f, 0.876f,
        0.554f, 0.635f, 0.951f,
        0.717f, 0.771f, 0.912f,
        0.865f, 0.865f, 0.865f,
        0.906f, 0.740f, 0.679f,
        0.887f, 0.583f, 0.485f,
        0.813f, 0.395f, 0.311f,
        0.706f, 0.016f, 0.150f,
    ];

    /// <summary>The colour a value with NO position takes — NaN (infinite fatigue
    /// life, the VTU "no value" convention), or a non-positive value under a log-scale
    /// display. A neutral MID grey, deliberately unlike anything a map produces:
    /// viridis runs dark purple to yellow with nothing desaturated, and the diverging
    /// map's neutral midpoint is a much LIGHTER grey (0.865 against 0.5), so "no data"
    /// cannot be mistaken for "the crossing". Before this existed, NaN fell through
    /// <see cref="Sample(FieldColorMap, double)"/>'s clamp to the map's BOTTOM stop —
    /// which on a life plot painted an immortal node the colour of the shortest-lived
    /// one, the worst direction for the confusion to run.</summary>
    public static readonly (float R, float G, float B) NoValueColor = (0.5f, 0.5f, 0.5f);

    /// <summary>The stop table behind a map — one RGB triple per stop, evenly spaced
    /// over [0, 1]. Exposed so the legend draws the same colours the fills do rather
    /// than re-sampling at its own positions.</summary>
    public static IReadOnlyList<float> Stops(FieldColorMap map) =>
        map == FieldColorMap.Diverging ? DivergingStops : ViridisStops;

    /// <summary>How many stops a map's table has.</summary>
    public static int StopCount(FieldColorMap map) => Stops(map).Count / 3;

    /// <summary>
    /// The colour at <paramref name="t"/> in [0, 1] (clamped), linearly interpolated
    /// between the table's stops. <c>t = 0</c> and <c>t = 1</c> return the first and
    /// last stops exactly.
    /// </summary>
    public static (float R, float G, float B) Sample(FieldColorMap map, double t)
    {
        // NaN is "no value", not "small": it takes the dedicated grey rather than
        // falling through the clamp to the bottom stop. One rule here covers every
        // route in — the range overload (Normalize(NaN) is NaN) and the log-scale
        // colour path (a non-positive value has no log position) both arrive as NaN t.
        if (double.IsNaN(t))
            return NoValueColor;
        var stops = Stops(map);
        int last = stops.Count / 3 - 1;
        if (!(t > 0))
            return (stops[0], stops[1], stops[2]);
        if (t >= 1)
            return (stops[last * 3], stops[last * 3 + 1], stops[last * 3 + 2]);

        double scaled = t * last;
        int i = (int)scaled;
        // Guard the exact-1 boundary the comparison above already covers, plus the
        // rounding that can put `scaled` a hair above `last` for t just under 1.
        if (i >= last)
            return (stops[last * 3], stops[last * 3 + 1], stops[last * 3 + 2]);
        float f = (float)(scaled - i);
        int a = i * 3;
        int b = a + 3;
        return (
            stops[a] + (stops[b] - stops[a]) * f,
            stops[a + 1] + (stops[b + 1] - stops[a + 1]) * f,
            stops[a + 2] + (stops[b + 2] - stops[a + 2]) * f);
    }

    /// <summary>The colour a value maps to, through a range and a map — the one call
    /// that puts <c>FieldRange.Normalize</c> and <see cref="Sample"/> together, so the
    /// fills, the legend and any probe readout cannot disagree about a value's
    /// colour.</summary>
    public static (float R, float G, float B) Sample(
        FieldColorMap map, in EngrCAD.Mesh.FieldRange range, double value) =>
        Sample(map, range.Normalize(value));
}
