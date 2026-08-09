using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Implicit;

namespace EngrCAD.Modeling;

/// <summary>
/// A 3D Hilbert curve clipped to a solid — the volume consumer of
/// <see cref="SpaceFillingCurve3d"/>: ONE connected route through the whole interior, which is
/// what a single-extrusion-path print, a single-channel cooling passage or a one-piece heater
/// element wants and what no lattice in the implicit engine can express (a gyroid is a SURFACE,
/// not a path).
///
/// <para><b>It rides the same two seams the 2D consumer established.</b> The clip is a
/// COMPARISON against an exact signed distance — an <see cref="Sdf"/> is sign-exact, so "is this
/// point at least <c>clearance</c> inside the wall" needs no tolerance — and what is reported is
/// what was MEASURED rather than what the path length implies.</para>
///
/// <para><b>The 3D placement question is the one thing that is genuinely new, and it is stated
/// rather than solved.</b> The footprint is the solid's bounding CUBE, so a long thin part
/// wastes the curve exactly as a long thin plate wastes the 2D one — and the 2D answer (tile
/// square blocks, <see cref="SpaceFillingCurve.OverTiled"/>) does carry over in principle, since
/// a 3D Hilbert block also runs between two ADJACENT CORNERS of its cube and so tiles. It is not
/// built because nothing asks for it yet; <see cref="Waste"/> reports what the cube cost, so the
/// decision is a number rather than a guess. The per-LAYER alternative sidesteps the question
/// entirely by keeping the 2D placement per slice — <see cref="SpaceFillingInfill.Fill"/> over
/// <c>Shape.Section</c>, which is a different deliverable (one path per layer, not one path)
/// and is shown in the docs rather than wrapped here.</para>
///
/// <para><b>Both ways a fill can silently miss are refused by name</b>, as in 2D, with the
/// instrument stated: there is no 3D erosion to take connected pieces of, so "is there room at
/// all" is answered by a PROBE GRID at half the achieved spacing. A solid with no probe point
/// far enough inside is too thin for this spacing; a solid with such a point but no CURVE point
/// is one the lattice's phase stepped over. Those are different mistakes with different fixes,
/// so they get different messages.</para>
/// </summary>
public static class SolidInfill
{
    /// <summary>
    /// Fills <paramref name="solid"/> with a 3D Hilbert curve at a spacing at or under
    /// <paramref name="spacing"/>.
    /// </summary>
    /// <param name="solid">The body to fill. Lowered to its implicit field once — the clip
    /// needs a signed distance, and `Explain(ToImplicit)` says what that costs for this
    /// shape.</param>
    /// <param name="spacing">The largest acceptable distance between neighbouring passes. The
    /// achieved spacing is reported and is never coarser.</param>
    /// <param name="clearance">How far inside the wall the path must stay. Null takes half the
    /// ACHIEVED spacing, so a bead of that width just touches the surface.</param>
    /// <param name="maxSites">Refusal cap passed to the generator.</param>
    public static SolidInfillPath Fill(
        Shape solid, double spacing, double? clearance = null,
        int maxSites = SpaceFillingCurve3d.DefaultMaxSites)
    {
        ArgumentNullException.ThrowIfNull(solid);
        if (!(spacing > 0) || !double.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), "The infill spacing must be positive and finite.");
        if (clearance is { } stated && (!(stated >= 0) || !double.IsFinite(stated)))
            throw new ArgumentOutOfRangeException(nameof(clearance), "The wall clearance must be zero or positive.");

        var field = solid.ToImplicit();
        return Fill(field, field.Bounds, spacing, clearance, maxSites);
    }

    /// <summary>
    /// Fills the region of <paramref name="field"/> inside <paramref name="bounds"/> — the
    /// field overload, for a caller who already has one (a lattice, a blend, a
    /// <c>MeshSdf</c>) and does not want a second lowering.
    /// </summary>
    public static SolidInfillPath Fill(
        Sdf field, in Aabb bounds, double spacing, double? clearance = null,
        int maxSites = SpaceFillingCurve3d.DefaultMaxSites)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (bounds.IsEmpty)
            throw new ArgumentException("The solid to fill has no bounds.", nameof(bounds));

        var curve = SpaceFillingCurve3d.Over(bounds, spacing, maxSites);
        double wallClearance = clearance ?? curve.Spacing / 2;

        var points = curve.Points;
        var samples = new Vector3d[points.Count];
        for (int i = 0; i < points.Count; i++)
            samples[i] = points[i];
        var distances = new double[points.Count];
        field.Evaluate(samples, distances);

        var runs = new List<IReadOnlyList<Vector3d>>();
        var current = new List<Vector3d>();
        int kept = 0;
        for (int i = 0; i < points.Count; i++)
        {
            // Exact decision: the field's sign is exact and this is a comparison against a
            // stated clearance rather than a tolerance band.
            if (distances[i] <= -wallClearance)
            {
                current.Add(points[i]);
                kept++;
                continue;
            }
            if (current.Count > 0)
            {
                runs.Add(current);
                current = [];
            }
        }
        if (current.Count > 0)
            runs.Add(current);

        if (kept == 0)
        {
            // Which of the two mistakes it is, decided by a finer instrument rather than
            // guessed: a probe grid at half the achieved spacing says whether ANY point of the
            // solid keeps the clearance.
            double deepest = DeepestProbe(field, bounds, curve.Spacing / 2);
            throw deepest <= -wallClearance
                ? new ArgumentException(
                    $"The Hilbert curve at the achieved spacing {curve.Spacing} (asked for {spacing}) "
                    + $"put no point at least {wallClearance} inside this solid, although a probe at "
                    + $"half that spacing reaches {-deepest} in: the lattice's phase stepped over the "
                    + "room there is. Reduce the spacing.",
                    nameof(field))
                : new ArgumentException(
                    $"No point of this solid is more than {wallClearance} from its own surface (the "
                    + $"deepest a probe found is {-deepest}), so a fill at the achieved spacing "
                    + $"{curve.Spacing} (asked for {spacing}) would miss it entirely. Reduce the "
                    + "spacing, or pass clearance: 0 to fill to the surface.",
                    nameof(field));
        }

        return new SolidInfillPath(curve, wallClearance, runs, bounds);
    }

    /// <summary>The most negative value a regular probe grid of <paramref name="step"/> finds —
    /// "how far inside does this solid ever get", the instrument that tells a solid too thin to
    /// fill from one the lattice merely stepped over. A grid rather than an erosion because
    /// there is no 3D counterpart to <c>Region2dOffset</c>; it is finer than the curve by
    /// construction, so a positive answer really is about the shape rather than the phase.</summary>
    private static double DeepestProbe(Sdf field, in Aabb bounds, double step)
    {
        var size = bounds.Max - bounds.Min;
        int nx = Math.Max(2, (int)Math.Ceiling(size.X / step));
        int ny = Math.Max(2, (int)Math.Ceiling(size.Y / step));
        int nz = Math.Max(2, (int)Math.Ceiling(size.Z / step));
        // A cap on the probe, so an absurdly fine clearance cannot turn a refusal message into
        // a long computation: the message only has to be RIGHT about which mistake it is.
        const int cap = 64;
        nx = Math.Min(nx, cap);
        ny = Math.Min(ny, cap);
        nz = Math.Min(nz, cap);

        var probes = new Vector3d[nx * ny * nz];
        int at = 0;
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        for (int k = 0; k < nz; k++)
        {
            probes[at++] = new Vector3d(
                bounds.Min.X + size.X * (i + 0.5) / nx,
                bounds.Min.Y + size.Y * (j + 0.5) / ny,
                bounds.Min.Z + size.Z * (k + 0.5) / nz);
        }
        var values = new double[probes.Length];
        field.Evaluate(probes, values);
        double deepest = double.PositiveInfinity;
        foreach (double v in values)
            deepest = Math.Min(deepest, v);
        return deepest;
    }
}

/// <summary>
/// One 3D Hilbert fill of a solid: the generator's report, the path broken into the RUNS the
/// clip left, and the measurements a caller needs to believe it — see <see cref="SolidInfill"/>.
/// </summary>
public sealed class SolidInfillPath
{
    internal SolidInfillPath(
        SpaceFillingCurve3d curve, double clearance,
        IReadOnlyList<IReadOnlyList<Vector3d>> runs, in Aabb bounds)
    {
        Curve = curve;
        Clearance = clearance;
        Runs = runs;
        SolidBounds = bounds;

        double length = 0;
        int points = 0;
        int isolated = 0;
        foreach (var run in runs)
        {
            points += run.Count;
            if (run.Count < 2)
                isolated++;
            for (int i = 1; i < run.Count; i++)
                length += run[i].DistanceTo(run[i - 1]);
        }
        Length = length;
        PointCount = points;
        IsolatedPoints = isolated;
    }

    /// <summary>The generator's own report — order, requested and ACHIEVED spacing.</summary>
    public SpaceFillingCurve3d Curve { get; }

    /// <summary>The finite order the spacing request resolved to.</summary>
    public int Order => Curve.Order;

    /// <summary>The spacing the caller asked for.</summary>
    public double RequestedSpacing => Curve.RequestedSpacing;

    /// <summary>The spacing ACHIEVED — never coarser than <see cref="RequestedSpacing"/>.</summary>
    public double Spacing => Curve.Spacing;

    /// <summary>How far inside the surface the path was required to stay.</summary>
    public double Clearance { get; }

    /// <summary>The solid's own bounds, kept so <see cref="Waste"/> can say what the cube cost.</summary>
    public Aabb SolidBounds { get; }

    /// <summary>The path, broken where the clip left the solid: within a run, consecutive points
    /// are exactly <see cref="Spacing"/> apart. Between runs the tool travels —
    /// see <see cref="Link"/>.</summary>
    public IReadOnlyList<IReadOnlyList<Vector3d>> Runs { get; }

    /// <summary>Total drawn length, excluding the moves between runs.</summary>
    public double Length { get; }

    /// <summary>How many curve points survived the clip.</summary>
    public int PointCount { get; }

    /// <summary>Runs of a single point: a cell the curve reaches but cannot draw through.
    /// Reported rather than dropped.</summary>
    public int IsolatedPoints { get; }

    /// <summary>The number of travel moves between runs.</summary>
    public int TravelMoves => Math.Max(0, Runs.Count - 1);

    /// <summary>The share of the generated curve the clip threw away. On a cube this is only the
    /// clearance shell; on a long thin part it is most of the curve, because the footprint is the
    /// bounding CUBE — the placement cost, reported as a number so the tiled 3D footprint is a
    /// decision rather than a guess.</summary>
    public double Waste => 1.0 - (double)PointCount / Curve.Points.Count;

    /// <summary>The travel between runs, ordered — see <see cref="RunLinker"/>. The same linker
    /// the 2D fill uses, over the same measured baseline.</summary>
    public PathLinkage Link() => RunLinker.Link(RunLinker.EndsOf(Runs));
}
