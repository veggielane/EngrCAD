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
///
/// <para><b>Neither rule survives contact with a deliberately anisotropic element</b>, which
/// is what a boundary layer is made of: a tetrahedron cut from a prism 0.01 mm thick and 1 mm
/// wide has a minimum dihedral under a degree and a radius-edge ratio in the tens, and it is
/// exactly right. So the elements are PARTITIONED by their measured stretch
/// (<see cref="TetQualityOptions.AnisotropyThreshold"/>) and each half gets the rule that
/// means something for it: <see cref="SliverCount"/> and the radius-edge figures cover the
/// ISOTROPIC elements only, while the stretched ones are counted
/// (<see cref="AnisotropicCount"/>, <see cref="MaxStretch"/>) and measured in their OWN
/// metric (<see cref="MinStretchedDihedralDegrees"/> — the minimum dihedral after the element
/// is un-stretched along its thinnest principal axis). A mesh with no stretched elements
/// reports exactly what it always did, number for number.</para>
///
/// <para><b>What this partition can and cannot tell you, stated rather than implied.</b> A
/// legitimate boundary-layer element and an accidental sliver are AFFINELY EQUIVALENT — the
/// stack element is four nearly-coplanar points too — so no purely local geometric measure
/// separates them, and <see cref="MinStretchedDihedralDegrees"/> will rate an unintended
/// sliver just as well as a layer element. What distinguishes them is whether the thin
/// direction is shared with the neighbours and with the physics, which is intent, not
/// geometry. That is why <see cref="AnisotropicCount"/> is reported BESIDE the stretched
/// quality rather than instead of it: a mesh that was never meant to be anisotropic and
/// reports a large one is telling you something is wrong, and the layered mesher's own
/// <c>BoundaryLayerReport.ElementCount</c> is the number to check it against.</para>
/// </summary>
/// <param name="TetCount">Number of elements measured.</param>
/// <param name="MinDihedralDegrees">Smallest dihedral angle anywhere in the mesh.</param>
/// <param name="MaxDihedralDegrees">Largest dihedral angle anywhere in the mesh.</param>
/// <param name="MeanMinDihedralDegrees">Mean over elements of each element's smallest dihedral.</param>
/// <param name="MinAspectRatio">Worst normalized aspect measure, 3*inradius/circumradius (1 = regular).</param>
/// <param name="MeanAspectRatio">Mean normalized aspect measure.</param>
/// <param name="MaxRadiusEdgeRatio">
/// Worst circumradius-over-shortest-edge ratio, ISOTROPIC elements only — NaN when every
/// element is stretched, since there is then no population to report a worst case over.
/// </param>
/// <param name="MeanRadiusEdgeRatio">Mean radius-edge ratio, isotropic elements only (NaN as above).</param>
/// <param name="MinVolume">Smallest element volume.</param>
/// <param name="MaxVolume">Largest element volume.</param>
/// <param name="TotalVolume">Sum of element volumes.</param>
/// <param name="MinEdgeLength">Shortest edge anywhere.</param>
/// <param name="MaxEdgeLength">Longest edge anywhere.</param>
/// <param name="SliverCount">
/// ISOTROPIC elements whose minimum dihedral is below the sliver threshold. Stretched
/// elements are excluded because the rule does not apply to them, not because they are
/// assumed good — see <see cref="AnisotropicCount"/>.
/// </param>
/// <param name="WorstElement">
/// Index of the isotropic element with the smallest minimum dihedral (or, when every element
/// is stretched, of the stretched one with the worst un-stretched dihedral).
/// </param>
/// <param name="SliverAngleDegrees">The threshold <see cref="SliverCount"/> was counted against.</param>
/// <param name="DihedralHistogram">Counts of element minimum dihedrals in 10-degree bins (18 bins, 0..180).</param>
/// <param name="RadiusEdgeHistogram">Counts of radius-edge ratios in bins [0,1), [1,1.5), [1.5,2), [2,3), [3,5), [5,inf), isotropic elements only.</param>
/// <param name="AnisotropicCount">
/// Elements whose longest edge exceeds <see cref="AnisotropyThreshold"/> times their shortest
/// — the ones the isotropic rules were NOT applied to. In a mesh built with a boundary layer
/// this should equal the layer's own element count; in one built without, any value above
/// zero is a finding.
/// </param>
/// <param name="MaxStretch">Largest longest-over-shortest edge ratio anywhere.</param>
/// <param name="MeanAnisotropicStretch">Mean stretch over the anisotropic elements (NaN when there are none).</param>
/// <param name="MinStretchedDihedralDegrees">
/// Smallest minimum dihedral among the ANISOTROPIC elements after each is un-stretched along
/// its own thinnest principal axis — "given that this element is deliberately thin, is it
/// otherwise well shaped?". NaN when there are no anisotropic elements.
/// </param>
/// <param name="MaxAnisotropicRadiusEdgeRatio">
/// Worst radius-edge ratio among the anisotropic elements, reported separately so nothing is
/// hidden: it is expected to be large and means nothing on its own. NaN when there are none.
/// </param>
/// <param name="AnisotropyThreshold">The stretch above which an element took the anisotropic path.</param>
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
    IReadOnlyList<int> RadiusEdgeHistogram,
    int AnisotropicCount = 0,
    double MaxStretch = 0,
    double MeanAnisotropicStretch = double.NaN,
    double MinStretchedDihedralDegrees = double.NaN,
    double MaxAnisotropicRadiusEdgeRatio = double.NaN,
    double AnisotropyThreshold = TetQualityOptions.DefaultAnisotropyThreshold)
{
    /// <summary>Bin edges for <see cref="RadiusEdgeHistogram"/>.</summary>
    public static ReadOnlySpan<double> RadiusEdgeBins => [1.0, 1.5, 2.0, 3.0, 5.0];

    /// <summary>Elements measured by the isotropic rules.</summary>
    public int IsotropicCount => TetCount - AnisotropicCount;

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
        text.AppendLine($"Radius-edge     max {MaxRadiusEdgeRatio:F3}, mean {MeanRadiusEdgeRatio:F3}" +
                        (AnisotropicCount > 0 ? $"   (over {IsotropicCount} isotropic elements)" : ""));
        text.AppendLine($"Slivers         {SliverCount} below {SliverAngleDegrees:F1} deg " +
                        $"(worst element {WorstElement})");

        if (AnisotropicCount > 0)
        {
            text.AppendLine($"Anisotropic     {AnisotropicCount} elements stretched over " +
                            $"{AnisotropyThreshold:F1}x (max {MaxStretch:F1}x, mean " +
                            $"{MeanAnisotropicStretch:F1}x)");
            text.AppendLine($"  un-stretched  min dihedral {MinStretchedDihedralDegrees:F2} deg" +
                            $"   (raw radius-edge max {MaxAnisotropicRadiusEdgeRatio:F1}, expected large)");
        }

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

/// <summary>Controls for <see cref="TetQuality.Analyze(TetMesh, TetQualityOptions)"/>.</summary>
public sealed record TetQualityOptions
{
    /// <summary>
    /// The default stretch above which an element takes the anisotropic path. Chosen so that
    /// ordinary isotropic meshing never reaches it — a Kuhn-subdivided cube's worst element
    /// is sqrt(3) = 1.73, and Delaunay refinement to a radius-edge bound of 2.0 stays well
    /// under 4 — while a boundary layer's first row clears it by an order of magnitude. It is
    /// deliberately a round number rather than a tuned one: a threshold fitted to whatever a
    /// fixture happened to measure would pass everything and protect nothing.
    /// </summary>
    public const double DefaultAnisotropyThreshold = 4.0;

    /// <summary>
    /// Minimum-dihedral threshold below which an ISOTROPIC element counts as a sliver. The
    /// default of 10 degrees is the usual practical floor for linear tetrahedra; raise it to
    /// see how much of the mesh sits near whatever bar the downstream solver actually needs.
    /// </summary>
    public double SliverAngleDegrees { get; init; } = 10.0;

    /// <summary>
    /// Longest-over-shortest edge ratio above which an element is measured as deliberately
    /// anisotropic instead of by the isotropic rules. Set it to
    /// <see cref="double.PositiveInfinity"/> to measure every element the old way.
    /// </summary>
    public double AnisotropyThreshold { get; init; } = DefaultAnisotropyThreshold;
}

/// <summary>Computes <see cref="TetQualityReport"/>s.</summary>
public static class TetQuality
{
    /// <summary>
    /// Measures every element of <paramref name="mesh"/>.
    /// </summary>
    /// <param name="mesh">The mesh to measure.</param>
    /// <param name="sliverAngleDegrees">
    /// Minimum-dihedral threshold below which an isotropic element counts as a sliver.
    /// </param>
    public static TetQualityReport Analyze(TetMesh mesh, double sliverAngleDegrees = 10.0) =>
        Analyze(mesh, new TetQualityOptions { SliverAngleDegrees = sliverAngleDegrees });

    /// <summary>Measures every element of <paramref name="mesh"/> under <paramref name="options"/>.</summary>
    public static TetQualityReport Analyze(TetMesh mesh, TetQualityOptions options)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);
        if (mesh.TetCount == 0)
            throw new ArgumentException("Cannot measure an empty mesh.", nameof(mesh));

        double minDihedral = double.PositiveInfinity, maxDihedral = double.NegativeInfinity;
        double sumMinDihedral = 0;
        double minAspect = double.PositiveInfinity, sumAspect = 0;
        double maxRadiusEdge = 0, sumRadiusEdge = 0;
        int radiusEdgeSamples = 0;
        double minVolume = double.PositiveInfinity, maxVolume = 0, totalVolume = 0;
        double minEdge = double.PositiveInfinity, maxEdge = 0;
        int slivers = 0;
        int worst = 0;
        double worstAngle = double.PositiveInfinity;

        int anisotropic = 0;
        double maxStretch = 0, sumAnisotropicStretch = 0;
        double minStretchedDihedral = double.PositiveInfinity;
        double maxAnisotropicRadiusEdge = 0;
        int anisotropicWorst = 0;
        double anisotropicWorstAngle = double.PositiveInfinity;

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
            dihedralHistogram[Math.Clamp((int)(elementMinDegrees / 10.0), 0, 17)]++;

            double shortest = TetGeometry.ShortestEdge(a, b, c, d);
            double longest = TetGeometry.LongestEdge(a, b, c, d);
            double stretch = shortest > 0 ? longest / shortest : double.PositiveInfinity;
            maxStretch = Math.Max(maxStretch, stretch);
            minEdge = Math.Min(minEdge, shortest);
            maxEdge = Math.Max(maxEdge, longest);

            double radiusEdge = TetGeometry.RadiusEdgeRatio(a, b, c, d);
            bool stretched = stretch > options.AnisotropyThreshold;

            if (stretched)
            {
                anisotropic++;
                sumAnisotropicStretch += stretch;
                if (!double.IsPositiveInfinity(radiusEdge))
                    maxAnisotropicRadiusEdge = Math.Max(maxAnisotropicRadiusEdge, radiusEdge);

                double aligned = TetGeometry.UnstretchedMinDihedralDegrees(a, b, c, d);
                minStretchedDihedral = Math.Min(minStretchedDihedral, aligned);
                if (aligned < anisotropicWorstAngle)
                {
                    anisotropicWorstAngle = aligned;
                    anisotropicWorst = t;
                }
            }
            else
            {
                if (elementMinDegrees < worstAngle)
                {
                    worstAngle = elementMinDegrees;
                    worst = t;
                }
                if (elementMinDegrees < options.SliverAngleDegrees)
                    slivers++;
                if (!double.IsPositiveInfinity(radiusEdge))
                {
                    maxRadiusEdge = Math.Max(maxRadiusEdge, radiusEdge);
                    sumRadiusEdge += radiusEdge;
                }
                radiusEdgeSamples++;
                radiusEdgeHistogram[BinFor(radiusEdge)]++;
            }

            double aspect = TetGeometry.AspectRatio(a, b, c, d);
            minAspect = Math.Min(minAspect, aspect);
            sumAspect += aspect;

            double volume = mesh.TetVolume(t);
            minVolume = Math.Min(minVolume, volume);
            maxVolume = Math.Max(maxVolume, volume);
            totalVolume += volume;
        }

        return new TetQualityReport(
            TetCount: mesh.TetCount,
            MinDihedralDegrees: minDihedral,
            MaxDihedralDegrees: maxDihedral,
            MeanMinDihedralDegrees: sumMinDihedral / mesh.TetCount,
            MinAspectRatio: minAspect,
            MeanAspectRatio: sumAspect / mesh.TetCount,
            // NaN rather than 0 when EVERY element is stretched: there is no isotropic
            // population to report a worst case over, and a bare 0.000 reads as perfect.
            MaxRadiusEdgeRatio: radiusEdgeSamples > 0 ? maxRadiusEdge : double.NaN,
            MeanRadiusEdgeRatio: radiusEdgeSamples > 0 ? sumRadiusEdge / radiusEdgeSamples : double.NaN,
            MinVolume: minVolume,
            MaxVolume: maxVolume,
            TotalVolume: totalVolume,
            MinEdgeLength: minEdge,
            MaxEdgeLength: maxEdge,
            SliverCount: slivers,
            WorstElement: radiusEdgeSamples > 0 ? worst : anisotropicWorst,
            SliverAngleDegrees: options.SliverAngleDegrees,
            DihedralHistogram: dihedralHistogram,
            RadiusEdgeHistogram: radiusEdgeHistogram,
            AnisotropicCount: anisotropic,
            MaxStretch: maxStretch,
            MeanAnisotropicStretch: anisotropic > 0 ? sumAnisotropicStretch / anisotropic : double.NaN,
            MinStretchedDihedralDegrees: anisotropic > 0 ? minStretchedDihedral : double.NaN,
            MaxAnisotropicRadiusEdgeRatio: anisotropic > 0 ? maxAnisotropicRadiusEdge : double.NaN,
            AnisotropyThreshold: options.AnisotropyThreshold);
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
