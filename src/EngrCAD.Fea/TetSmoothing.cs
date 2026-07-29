using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>Controls for <see cref="TetSmoothing.Smooth(TetMesh, TetSmoothOptions?)"/>.</summary>
public sealed record TetSmoothOptions
{
    /// <summary>Sweeps over the movable vertices. Each sweep is a full pass in index order.</summary>
    public int Passes { get; init; } = 5;

    /// <summary>
    /// Elements stretched beyond this (longest edge over shortest) are treated as DELIBERATELY
    /// anisotropic, and every vertex touching one is frozen. Matches
    /// <see cref="TetQualityOptions.AnisotropyThreshold"/>, and it must, or the two halves of
    /// the codebase would disagree about what a boundary layer is.
    /// </summary>
    public double AnisotropyThreshold { get; init; } = TetQualityOptions.DefaultAnisotropyThreshold;

    /// <summary>
    /// A move must raise its vertex's worst incident dihedral by at least this many degrees to
    /// be taken. A strict floor rather than a tolerance: it is what makes the sweep terminate,
    /// the monotone-decrease rule this repository uses elsewhere.
    /// </summary>
    public double MinImprovementDegrees { get; init; } = 1e-4;

    /// <summary>Pattern-search steps per vertex per pass, each halving the previous stride.</summary>
    public int StepsPerVertex { get; init; } = 8;

    /// <summary>
    /// A vertex whose worst incident dihedral already exceeds this is left alone.
    ///
    /// <para>This is what makes the pass a SLIVER remover rather than a global optimizer, and
    /// the difference is mostly cost: a well-shaped tetrahedral mesh has a mean minimum
    /// dihedral around 44 degrees, so a 25-degree gate skips the great majority of vertices
    /// after the first sweep and later sweeps become nearly free. It also perturbs less
    /// geometry to buy the same answer, which is worth having on its own.</para>
    /// </summary>
    public double TargetDihedralDegrees { get; init; } = 25.0;
}

/// <summary>What a smoothing run did.</summary>
/// <param name="Passes">Sweeps actually run (it stops early once nothing moves).</param>
/// <param name="VerticesMoved">Distinct vertices that ended up somewhere new.</param>
/// <param name="MovableVertices">Interior, isotropic vertices it was allowed to move.</param>
/// <param name="FrozenBoundaryVertices">Vertices left alone because they are on the boundary.</param>
/// <param name="FrozenAnisotropicVertices">
/// Vertices left alone because they touch a deliberately stretched element — the boundary-layer
/// partition, honoured so a layer is never "repaired" into isotropy.
/// </param>
/// <param name="MinDihedralBefore">Worst dihedral anywhere before, in degrees.</param>
/// <param name="MinDihedralAfter">Worst dihedral anywhere after, in degrees.</param>
/// <param name="SliversBefore">Isotropic elements under the sliver angle before.</param>
/// <param name="SliversAfter">Isotropic elements under the sliver angle after.</param>
/// <param name="VolumeChangeRelative">
/// Relative change in total volume. Mathematically ZERO — the boundary never moves and the
/// elements keep tiling the same region — so this is a pure round-off measurement and a
/// non-trivial value means something is wrong.
/// </param>
public readonly record struct TetSmoothReport(
    int Passes,
    int VerticesMoved,
    int MovableVertices,
    int FrozenBoundaryVertices,
    int FrozenAnisotropicVertices,
    double MinDihedralBefore,
    double MinDihedralAfter,
    int SliversBefore,
    int SliversAfter,
    double VolumeChangeRelative)
{
    /// <summary>A one-line human summary.</summary>
    public override string ToString() =>
        $"{Passes} pass(es), {VerticesMoved}/{MovableVertices} vertices moved " +
        $"({FrozenBoundaryVertices} boundary + {FrozenAnisotropicVertices} anisotropic frozen); " +
        $"min dihedral {MinDihedralBefore:F2} -> {MinDihedralAfter:F2} deg, " +
        $"slivers {SliversBefore} -> {SliversAfter}, volume drift {VolumeChangeRelative:E2}";
}

/// <summary>
/// Optimization-based smoothing: moves INTERIOR vertices to raise the worst dihedral angle,
/// leaving the topology and the boundary exactly as they were.
///
/// <para><b>Why this exists.</b> Bounding the radius-edge ratio — which is all Delaunay
/// refinement can do — provably cannot exclude the SLIVER, four nearly-coplanar vertices whose
/// circumradius and shortest edge are both perfectly ordinary. So a mesh can carry an excellent
/// radius-edge histogram and still condition a stiffness matrix badly, and `TetQualityReport`
/// reports the minimum dihedral beside radius-edge precisely because the first measure cannot
/// see it. This is the post-pass that acts on what it sees.</para>
///
/// <para><b>Why smoothing rather than exudation.</b> The two standard answers are sliver
/// exudation (a weighted-Delaunay perturbation, which changes the TOPOLOGY) and
/// optimization-based smoothing (which moves points only). Only the second keeps every
/// guarantee the mesher already makes without re-deriving any of them: the boundary is
/// untouched so the surface-fidelity contract and the volume identity hold by construction,
/// the connectivity is untouched so nothing has to be re-classified or re-recovered, and every
/// candidate position is accepted only if it leaves all incident elements strictly positively
/// oriented BY THE EXACT PREDICATE, so `TetMesh`'s orientation invariant is preserved rather
/// than re-checked and hoped for. Exudation is the stronger technique and is still the filed
/// next step; it is also the one that can invalidate all of the above at once.</para>
///
/// <para><b>Three rules make it safe, and each one is load-bearing.</b></para>
/// <list type="number">
/// <item><b>Boundary vertices never move.</b> That is what makes the volume identity exact
/// rather than approximate — the elements go on tiling the same region — and it is why a
/// smoothed mesh still satisfies the surface-fidelity contract without a second check.</item>
/// <item><b>Vertices touching a deliberately anisotropic element never move.</b> A quality
/// report that cries wolf on correct output is worse than none, and a smoother that
/// "repairs" a boundary layer into isotropy is the same mistake with teeth: it would destroy
/// the resolution the layer exists to provide. The threshold is
/// <see cref="TetQualityOptions.AnisotropyThreshold"/>, shared, so the two cannot drift.
/// Note the honest limit this inherits: a layer element and an accidental sliver are AFFINELY
/// EQUIVALENT, so freezing by measured stretch necessarily also freezes accidental slivers
/// that happen to be stretched. Intent is not recoverable from geometry, and the report says
/// how many vertices were frozen for this reason so the cost is visible.</item>
/// <item><b>A move must strictly improve, by a floor.</b> The objective is the worst dihedral
/// over the vertex's incident elements, so a monotone-increase rule terminates and cannot
/// oscillate.</item>
/// </list>
///
/// <para><b>Deterministic.</b> Vertices are visited in ascending index order, the search
/// directions are a fixed table, the stride schedule is fixed halving, and there is no RNG and
/// no parallelism — so two runs produce bit-identical positions.</para>
/// </summary>
public static class TetSmoothing
{
    /// <summary>The six axis directions plus the four body diagonals — a fixed pattern.</summary>
    private static readonly Vector3d[] SearchDirections =
    [
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
        new(0.5773502691896258, 0.5773502691896258, 0.5773502691896258),
        new(-0.5773502691896258, -0.5773502691896258, -0.5773502691896258),
        new(0.5773502691896258, -0.5773502691896258, 0.5773502691896258),
        new(-0.5773502691896258, 0.5773502691896258, -0.5773502691896258),
    ];

    /// <summary>Smooths <paramref name="mesh"/>, returning a new mesh.</summary>
    public static TetMesh Smooth(TetMesh mesh, TetSmoothOptions? options = null) =>
        Smooth(mesh, options, out _);

    /// <summary>Smooths <paramref name="mesh"/>, reporting what it did.</summary>
    public static TetMesh Smooth(TetMesh mesh, TetSmoothOptions? options, out TetSmoothReport report)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new TetSmoothOptions();
        if (options.Passes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.Passes,
                "Passes cannot be negative.");
        if (!(options.AnisotropyThreshold > 1))
            throw new ArgumentOutOfRangeException(nameof(options), options.AnisotropyThreshold,
                "The anisotropy threshold is a longest-over-shortest edge ratio, so it must exceed 1.");

        int vertexCount = mesh.VertexCount;
        int tetCount = mesh.TetCount;

        var positions = new Vector3d[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            positions[v] = mesh.Position(v);

        var tets = new int[4 * tetCount];
        var regions = new int[tetCount];
        for (int t = 0; t < tetCount; t++)
        {
            var tet = mesh.GetTet(t);
            tets[4 * t] = tet.A;
            tets[4 * t + 1] = tet.B;
            tets[4 * t + 2] = tet.C;
            tets[4 * t + 3] = tet.D;
            regions[t] = mesh.RegionOf(t);
        }
        var boundary = mesh.BoundaryFacets.ToArray();

        // --- who may move ---
        var frozenBoundary = new bool[vertexCount];
        foreach (var f in boundary)
        {
            frozenBoundary[f.V0] = true;
            frozenBoundary[f.V1] = true;
            frozenBoundary[f.V2] = true;
        }

        var frozenAnisotropic = new bool[vertexCount];
        for (int t = 0; t < tetCount; t++)
        {
            var a = positions[tets[4 * t]];
            var b = positions[tets[4 * t + 1]];
            var c = positions[tets[4 * t + 2]];
            var d = positions[tets[4 * t + 3]];
            double shortest = TetGeometry.ShortestEdge(a, b, c, d);
            double stretch = shortest > 0
                ? TetGeometry.LongestEdge(a, b, c, d) / shortest
                : double.PositiveInfinity;
            if (stretch <= options.AnisotropyThreshold)
                continue;
            for (int i = 0; i < 4; i++)
                frozenAnisotropic[tets[4 * t + i]] = true;
        }

        // --- vertex -> incident elements, as a CSR-style flat table (no per-vertex lists) ---
        var incidentStart = new int[vertexCount + 1];
        for (int i = 0; i < tets.Length; i++)
            incidentStart[tets[i] + 1]++;
        for (int v = 0; v < vertexCount; v++)
            incidentStart[v + 1] += incidentStart[v];
        var incident = new int[tets.Length];
        var cursor = (int[])incidentStart.Clone();
        for (int t = 0; t < tetCount; t++)
            for (int i = 0; i < 4; i++)
                incident[cursor[tets[4 * t + i]]++] = t;

        int boundaryFrozen = 0, anisotropicFrozen = 0, movable = 0;
        for (int v = 0; v < vertexCount; v++)
        {
            if (frozenBoundary[v]) boundaryFrozen++;
            else if (frozenAnisotropic[v]) anisotropicFrozen++;
            else movable++;
        }

        var before = TetQuality.Analyze(mesh, new TetQualityOptions
        {
            AnisotropyThreshold = options.AnisotropyThreshold,
        });
        double volumeBefore = mesh.Volume;

        // A stride that means the same thing at any model scale.
        double extent = mesh.Bounds.Size.Length;

        var moved = new bool[vertexCount];
        int passesRun = 0;
        for (int pass = 0; pass < options.Passes; pass++)
        {
            bool anyMoved = false;
            passesRun++;

            for (int v = 0; v < vertexCount; v++)
            {
                if (frozenBoundary[v] || frozenAnisotropic[v])
                    continue;
                int from = incidentStart[v], to = incidentStart[v + 1];
                if (to == from)
                    continue;

                double best = WorstIncidentDihedral(positions, tets, incident, from, to);
                if (best >= options.TargetDihedralDegrees)
                    continue;

                var origin = positions[v];
                var bestPosition = origin;

                // Stride starts at a fraction of the shortest incident edge, so the first trial
                // is always a small perturbation of a real length in this neighbourhood.
                double stride = 0.25 * ShortestIncidentEdge(positions, tets, incident, from, to, v);
                if (!(stride > 0))
                    stride = 0.25 * extent;

                for (int step = 0; step < options.StepsPerVertex; step++)
                {
                    bool improvedThisStep = false;
                    foreach (var direction in SearchDirections)
                    {
                        var candidate = bestPosition + direction * stride;
                        positions[v] = candidate;

                        if (AllPositivelyOriented(positions, tets, incident, from, to))
                        {
                            double score = WorstIncidentDihedral(positions, tets, incident, from, to);
                            if (score > best + options.MinImprovementDegrees)
                            {
                                best = score;
                                bestPosition = candidate;
                                improvedThisStep = true;
                            }
                        }
                        positions[v] = bestPosition;
                    }
                    if (!improvedThisStep)
                        stride *= 0.5;
                }

                positions[v] = bestPosition;
                if (bestPosition != origin)
                {
                    moved[v] = true;
                    anyMoved = true;
                }
            }

            if (!anyMoved)
                break;
        }

        var smoothed = new TetMesh(positions, tets, regions, boundary);
        var after = TetQuality.Analyze(smoothed, new TetQualityOptions
        {
            AnisotropyThreshold = options.AnisotropyThreshold,
        });

        int movedCount = 0;
        foreach (bool m in moved)
            if (m) movedCount++;

        report = new TetSmoothReport(
            Passes: passesRun,
            VerticesMoved: movedCount,
            MovableVertices: movable,
            FrozenBoundaryVertices: boundaryFrozen,
            FrozenAnisotropicVertices: anisotropicFrozen,
            MinDihedralBefore: before.MinDihedralDegrees,
            MinDihedralAfter: after.MinDihedralDegrees,
            SliversBefore: before.SliverCount,
            SliversAfter: after.SliverCount,
            VolumeChangeRelative: Math.Abs(smoothed.Volume - volumeBefore)
                                  / Math.Max(Math.Abs(volumeBefore), 1e-300));
        return smoothed;
    }

    /// <summary>The worst dihedral, in degrees, over one vertex's incident elements.</summary>
    private static double WorstIncidentDihedral(
        Vector3d[] positions, int[] tets, int[] incident, int from, int to)
    {
        Span<double> angles = stackalloc double[6];
        double worst = double.PositiveInfinity;
        for (int i = from; i < to; i++)
        {
            int t = incident[i];
            TetGeometry.DihedralAngles(
                positions[tets[4 * t]], positions[tets[4 * t + 1]],
                positions[tets[4 * t + 2]], positions[tets[4 * t + 3]], angles);
            for (int k = 0; k < 6; k++)
                worst = Math.Min(worst, angles[k]);
        }
        return worst * 180.0 / Math.PI;
    }

    /// <summary>
    /// Whether every incident element is still strictly positively oriented — asked of the
    /// EXACT predicate, because this is what preserves <see cref="TetMesh"/>'s invariant. A
    /// floating-point volume test would admit an element the constructor then rejects.
    /// </summary>
    private static bool AllPositivelyOriented(
        Vector3d[] positions, int[] tets, int[] incident, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            int t = incident[i];
            if (Predicates3d.SignedVolume6Sign(
                    positions[tets[4 * t]], positions[tets[4 * t + 1]],
                    positions[tets[4 * t + 2]], positions[tets[4 * t + 3]]) <= 0)
                return false;
        }
        return true;
    }

    /// <summary>Shortest edge touching this vertex, which sets the initial search stride.</summary>
    private static double ShortestIncidentEdge(
        Vector3d[] positions, int[] tets, int[] incident, int from, int to, int vertex)
    {
        double shortest = double.PositiveInfinity;
        var p = positions[vertex];
        for (int i = from; i < to; i++)
        {
            int t = incident[i];
            for (int k = 0; k < 4; k++)
            {
                int other = tets[4 * t + k];
                if (other == vertex)
                    continue;
                shortest = Math.Min(shortest, (positions[other] - p).Length);
            }
        }
        return double.IsPositiveInfinity(shortest) ? 0 : shortest;
    }
}
