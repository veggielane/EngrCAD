using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// Element-quality statistics for a <see cref="TetMesh"/> — reported as a value, never
/// logged and never silently acted on.
///
/// <para><b>Two measures, because neither alone is honest.</b> The radius-edge ratio is what
/// Delaunay refinement can bound, and bounding it excludes every badly shaped tetrahedron
/// EXCEPT the sliver — four nearly-coplanar vertices, whose circumradius and shortest edge
/// are both perfectly ordinary. A mesh can therefore have an excellent radius-edge histogram
/// and still be useless for FEA. The minimum dihedral angle is what actually governs the
/// stiffness matrix's conditioning, and it is the number that sees slivers. Both are here,
/// and <see cref="SliverCount"/> counts what the first measure cannot.</para>
/// </summary>
/// <param name="TetCount">Number of elements measured.</param>
/// <param name="MinDihedralDegrees">Smallest dihedral angle anywhere in the mesh.</param>
/// <param name="MaxDihedralDegrees">Largest dihedral angle anywhere in the mesh.</param>
/// <param name="MeanMinDihedralDegrees">Mean over elements of each element's smallest dihedral.</param>
/// <param name="MinAspectRatio">Worst normalized aspect measure, 3*inradius/circumradius (1 = regular).</param>
/// <param name="MeanAspectRatio">Mean normalized aspect measure.</param>
/// <param name="MaxRadiusEdgeRatio">Worst circumradius-over-shortest-edge ratio.</param>
/// <param name="MeanRadiusEdgeRatio">Mean radius-edge ratio.</param>
/// <param name="MinVolume">Smallest element volume.</param>
/// <param name="MaxVolume">Largest element volume.</param>
/// <param name="TotalVolume">Sum of element volumes.</param>
/// <param name="MinEdgeLength">Shortest edge anywhere.</param>
/// <param name="MaxEdgeLength">Longest edge anywhere.</param>
/// <param name="SliverCount">Elements whose minimum dihedral is below the sliver threshold.</param>
/// <param name="WorstElement">Index of the element with the smallest minimum dihedral.</param>
/// <param name="SliverAngleDegrees">The threshold <see cref="SliverCount"/> was counted against.</param>
/// <param name="DihedralHistogram">Counts of element minimum dihedrals in 10-degree bins (18 bins, 0..180).</param>
/// <param name="RadiusEdgeHistogram">Counts of radius-edge ratios in bins [0,1), [1,1.5), [1.5,2), [2,3), [3,5), [5,inf).</param>
public sealed record TetQualityReport(
    int TetCount,
    double MinDihedralDegrees,
    double MaxDihedralDegrees,
    double MeanMinDihedralDegrees,
    double MinAspectRatio,
    double MeanAspectRatio,
    double MaxRadiusEdgeRatio,
    double MeanRadiusEdgeRatio,
    double MinVolume,
    double MaxVolume,
    double TotalVolume,
    double MinEdgeLength,
    double MaxEdgeLength,
    int SliverCount,
    int WorstElement,
    double SliverAngleDegrees,
    IReadOnlyList<int> DihedralHistogram,
    IReadOnlyList<int> RadiusEdgeHistogram)
{
    /// <summary>Bin edges for <see cref="RadiusEdgeHistogram"/>.</summary>
    public static ReadOnlySpan<double> RadiusEdgeBins => [1.0, 1.5, 2.0, 3.0, 5.0];

    /// <summary>An aligned human-readable table, in the style of <c>SceneReport.ToText</c>.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"Tetrahedra      {TetCount}");
        text.AppendLine($"Volume          {TotalVolume:G8}  (min {MinVolume:G4}, max {MaxVolume:G4})");
        text.AppendLine($"Edge length     min {MinEdgeLength:G4}, max {MaxEdgeLength:G4}");
        text.AppendLine($"Dihedral (deg)  min {MinDihedralDegrees:F2}, max {MaxDihedralDegrees:F2}, " +
                        $"mean-min {MeanMinDihedralDegrees:F2}");
        text.AppendLine($"Aspect ratio    min {MinAspectRatio:F4}, mean {MeanAspectRatio:F4}   (1 = regular)");
        text.AppendLine($"Radius-edge     max {MaxRadiusEdgeRatio:F3}, mean {MeanRadiusEdgeRatio:F3}");
        text.AppendLine($"Slivers         {SliverCount} below {SliverAngleDegrees:F1} deg " +
                        $"(worst element {WorstElement})");

        text.AppendLine("Min-dihedral histogram (10 deg bins):");
        for (int i = 0; i < DihedralHistogram.Count; i++)
        {
            if (DihedralHistogram[i] == 0)
                continue;
            text.AppendLine($"  {i * 10,3}-{(i + 1) * 10,3}  {DihedralHistogram[i],8}");
        }

        text.AppendLine("Radius-edge histogram:");
        var labels = new[] { "< 1.0", "1.0-1.5", "1.5-2.0", "2.0-3.0", "3.0-5.0", ">= 5.0" };
        for (int i = 0; i < RadiusEdgeHistogram.Count && i < labels.Length; i++)
        {
            if (RadiusEdgeHistogram[i] == 0)
                continue;
            text.AppendLine($"  {labels[i],8}  {RadiusEdgeHistogram[i],8}");
        }
        return text.ToString();
    }
}

/// <summary>Computes <see cref="TetQualityReport"/>s.</summary>
public static class TetQuality
{
    /// <summary>
    /// Measures every element of <paramref name="mesh"/>.
    /// </summary>
    /// <param name="mesh">The mesh to measure.</param>
    /// <param name="sliverAngleDegrees">
    /// Minimum-dihedral threshold below which an element counts as a sliver. The default of
    /// 10 degrees is the usual practical floor for linear tetrahedra; raise it to see how
    /// much of the mesh sits near whatever bar the downstream solver actually needs.
    /// </param>
    public static TetQualityReport Analyze(TetMesh mesh, double sliverAngleDegrees = 10.0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TetCount == 0)
            throw new ArgumentException("Cannot measure an empty mesh.", nameof(mesh));

        double minDihedral = double.PositiveInfinity, maxDihedral = double.NegativeInfinity;
        double sumMinDihedral = 0;
        double minAspect = double.PositiveInfinity, sumAspect = 0;
        double maxRadiusEdge = 0, sumRadiusEdge = 0;
        double minVolume = double.PositiveInfinity, maxVolume = 0, totalVolume = 0;
        double minEdge = double.PositiveInfinity, maxEdge = 0;
        int slivers = 0;
        int worst = 0;
        double worstAngle = double.PositiveInfinity;

        var dihedralHistogram = new int[18];
        var radiusEdgeHistogram = new int[6];
        Span<double> angles = stackalloc double[6];

        for (int t = 0; t < mesh.TetCount; t++)
        {
            var tet = mesh.GetTet(t);
            var a = mesh.Position(tet.A);
            var b = mesh.Position(tet.B);
            var c = mesh.Position(tet.C);
            var d = mesh.Position(tet.D);

            TetGeometry.DihedralAngles(a, b, c, d, angles);
            double elementMin = angles[0], elementMax = angles[0];
            for (int i = 1; i < 6; i++)
            {
                elementMin = Math.Min(elementMin, angles[i]);
                elementMax = Math.Max(elementMax, angles[i]);
            }
            double elementMinDegrees = elementMin * 180.0 / Math.PI;
            double elementMaxDegrees = elementMax * 180.0 / Math.PI;

            minDihedral = Math.Min(minDihedral, elementMinDegrees);
            maxDihedral = Math.Max(maxDihedral, elementMaxDegrees);
            sumMinDihedral += elementMinDegrees;
            if (elementMinDegrees < worstAngle)
            {
                worstAngle = elementMinDegrees;
                worst = t;
            }
            if (elementMinDegrees < sliverAngleDegrees)
                slivers++;
            dihedralHistogram[Math.Clamp((int)(elementMinDegrees / 10.0), 0, 17)]++;

            double aspect = TetGeometry.AspectRatio(a, b, c, d);
            minAspect = Math.Min(minAspect, aspect);
            sumAspect += aspect;

            double radiusEdge = TetGeometry.RadiusEdgeRatio(a, b, c, d);
            if (!double.IsPositiveInfinity(radiusEdge))
            {
                maxRadiusEdge = Math.Max(maxRadiusEdge, radiusEdge);
                sumRadiusEdge += radiusEdge;
            }
            radiusEdgeHistogram[BinFor(radiusEdge)]++;

            double volume = mesh.TetVolume(t);
            minVolume = Math.Min(minVolume, volume);
            maxVolume = Math.Max(maxVolume, volume);
            totalVolume += volume;

            minEdge = Math.Min(minEdge, TetGeometry.ShortestEdge(a, b, c, d));
            maxEdge = Math.Max(maxEdge, TetGeometry.LongestEdge(a, b, c, d));
        }

        return new TetQualityReport(
            TetCount: mesh.TetCount,
            MinDihedralDegrees: minDihedral,
            MaxDihedralDegrees: maxDihedral,
            MeanMinDihedralDegrees: sumMinDihedral / mesh.TetCount,
            MinAspectRatio: minAspect,
            MeanAspectRatio: sumAspect / mesh.TetCount,
            MaxRadiusEdgeRatio: maxRadiusEdge,
            MeanRadiusEdgeRatio: sumRadiusEdge / mesh.TetCount,
            MinVolume: minVolume,
            MaxVolume: maxVolume,
            TotalVolume: totalVolume,
            MinEdgeLength: minEdge,
            MaxEdgeLength: maxEdge,
            SliverCount: slivers,
            WorstElement: worst,
            SliverAngleDegrees: sliverAngleDegrees,
            DihedralHistogram: dihedralHistogram,
            RadiusEdgeHistogram: radiusEdgeHistogram);
    }

    private static int BinFor(double radiusEdge)
    {
        var bins = TetQualityReport.RadiusEdgeBins;
        for (int i = 0; i < bins.Length; i++)
            if (radiusEdge < bins[i])
                return i;
        return bins.Length;
    }
}
