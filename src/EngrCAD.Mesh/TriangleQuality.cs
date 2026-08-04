using System.Text;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Triangle-shape statistics for a mesh — reported as a value, never logged and never
/// silently acted on (<c>TetQuality</c> in EngrCAD.Fea is the precedent, one project over).
///
/// <para><b>Why it exists.</b> Everything <see cref="Remesher"/> reports and everything its
/// tests assert is edge <i>length</i> — the <c>[0.66 L, 1.33 L]</c> band — and a length
/// distribution says nothing about shape. A remeshed box can sit at 95% of edges in band with
/// a worst triangle angle of 5.6°, and a plausible-looking variant of the long-edge flip guard
/// reached 99.7% in band at <b>0.02°</b>. Only a shape measure can see that, and until this
/// existed seeing it took a bespoke test helper.</para>
///
/// <para><b>Two measures, because neither alone is honest.</b> The minimum angle is what a
/// solver's conditioning and an interpolation error bound actually depend on, and it is the
/// number that sees a needle. The radius ratio <c>2r/R</c> is the normalized shape measure
/// (exactly 1 for an equilateral triangle, 0 for a degenerate one) and it is what sees a
/// triangle that is merely <i>flat</i> — a wide obtuse cap whose smallest angle is
/// unremarkable while its circumcircle is enormous, which is the shape that ruins a
/// circumcentre-based algorithm and a normal estimate alike.</para>
///
/// <para><b>The partition, and its honest limit.</b> A remesher cannot improve a triangle
/// whose every vertex is pinned: a collapse needs a free end, a flip needs one too, and
/// smoothing and projection skip fixed vertices outright. Remeshing a box with feature
/// detection on starts with <i>every</i> vertex on a crease, so <i>every</i> triangle is in
/// that state. Rating those against the same bar is a report that cries wolf on correct
/// output, so they are counted separately (<see cref="TriangleQualityReport.ConstrainedCount"/>)
/// and measured in their own right. What this cannot do is tell a constrained sliver from a
/// free one <i>geometrically</i> — they are the same triangle; the only difference is which
/// vertices the caller pinned, which is intent, not shape. And a <i>partially</i> pinned
/// triangle is genuinely in between (the remesher had some freedom, not all of it) with no
/// local measure able to say how much, so it is counted as FREE — the conservative side,
/// because the alternative would hide real defects behind constraints that were not actually
/// binding.</para>
///
/// <para><b>What is measured is what consumers see.</b> Non-triangular faces are split by
/// <see cref="PolygonFan"/>, the project's one rule for how a polygon becomes triangles, so
/// this measures the same triangles rendering, export, mass properties and every solver get.
/// A face with a zero-length edge has no shape at all and scores 0 on both measures rather
/// than being skipped.</para>
/// </summary>
public static class TriangleQuality
{
    /// <summary>
    /// Default angle below which a triangle is counted a sliver. 30° is the classical bar for
    /// a 2D triangulation — what Ruppert/Chew refinement is built to guarantee and roughly
    /// where interpolation error and matrix conditioning start to degrade visibly — against an
    /// isotropic remesh's own target of 60° (valence 6, equilateral). It is recorded on the
    /// report rather than assumed, so a caller who wants a different bar can say so and the
    /// number still means something.
    /// </summary>
    public const double DefaultSliverAngleDegrees = 30;

    /// <summary>Upper edges of the <see cref="TriangleQualityReport.RadiusRatioHistogram"/> bins.</summary>
    private static ReadOnlySpan<double> RatioBins => [0.1, 0.25, 0.5, 0.75, 0.9];

    /// <summary>
    /// Measures every triangle of <paramref name="mesh"/>.
    /// </summary>
    /// <param name="mesh">Mesh to measure; non-triangular faces are fanned by <see cref="PolygonFan"/>.</param>
    /// <param name="constrainedVertices">
    /// Vertices the producer was not free to move. A triangle all of whose corners are in this
    /// set is counted as constrained and kept out of the free population's figures — see the
    /// class remarks for exactly what that can and cannot distinguish. Null means nothing was
    /// pinned, which is the right answer for a mesh with no producer to ask.
    /// </param>
    /// <param name="sliverAngleDegrees">Angle below which a triangle counts as a sliver.</param>
    public static TriangleQualityReport Analyze(
        HalfEdgeMesh mesh,
        IReadOnlyCollection<int>? constrainedVertices = null,
        double sliverAngleDegrees = DefaultSliverAngleDegrees)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (sliverAngleDegrees is < 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(sliverAngleDegrees),
                sliverAngleDegrees, "Must be in [0, 60]: an equilateral triangle's angles are 60 degrees.");

        var pinned = constrainedVertices is null or { Count: 0 } ? null : new HashSet<int>(constrainedVertices);
        var (positions, faces) = mesh.ToIndexed();

        int triangles = 0, freeCount = 0, constrained = 0, slivers = 0, constrainedSlivers = 0;
        double minAngle = double.PositiveInfinity, meanMinAngle = 0;
        double minRatio = double.PositiveInfinity, meanRatio = 0;
        double minConstrainedAngle = double.PositiveInfinity;
        int worst = -1;
        var angleHistogram = new int[6];   // [0,5) [5,10) [10,20) [20,30) [30,45) [45,60]
        var ratioHistogram = new int[RatioBins.Length + 1];

        for (int f = 0; f < faces.Count; f++)
        {
            var loop = faces[f];
            int apex = PolygonFan.Apex(loop, positions);
            for (int i = 1; i < loop.Length - 1; i++)
            {
                var a = positions[loop[apex]];
                var b = positions[loop[PolygonFan.Corner(apex, loop.Length, i)]];
                var c = positions[loop[PolygonFan.Corner(apex, loop.Length, i + 1)]];
                var (angle, ratio) = Measure(a, b, c);

                bool isConstrained = pinned is not null &&
                                     pinned.Contains(loop[apex]) &&
                                     pinned.Contains(loop[PolygonFan.Corner(apex, loop.Length, i)]) &&
                                     pinned.Contains(loop[PolygonFan.Corner(apex, loop.Length, i + 1)]);
                triangles++;
                if (isConstrained)
                {
                    constrained++;
                    minConstrainedAngle = Math.Min(minConstrainedAngle, angle);
                    if (angle < sliverAngleDegrees)
                        constrainedSlivers++;
                    continue;
                }

                freeCount++;
                meanMinAngle += angle;
                meanRatio += ratio;
                if (angle < minAngle)
                {
                    minAngle = angle;
                    worst = f;
                }
                minRatio = Math.Min(minRatio, ratio);
                if (angle < sliverAngleDegrees)
                    slivers++;
                angleHistogram[AngleBin(angle)]++;
                ratioHistogram[RatioBin(ratio)]++;
            }
        }

        return new TriangleQualityReport(
            TriangleCount: triangles,
            MinAngleDegrees: freeCount == 0 ? double.NaN : minAngle,
            MeanMinAngleDegrees: freeCount == 0 ? double.NaN : meanMinAngle / freeCount,
            MinRadiusRatio: freeCount == 0 ? double.NaN : minRatio,
            MeanRadiusRatio: freeCount == 0 ? double.NaN : meanRatio / freeCount,
            SliverCount: slivers,
            WorstTriangle: worst,
            SliverAngleDegrees: sliverAngleDegrees,
            AngleHistogram: angleHistogram,
            RadiusRatioHistogram: ratioHistogram,
            ConstrainedCount: constrained,
            MinConstrainedAngleDegrees: constrained == 0 ? double.NaN : minConstrainedAngle,
            ConstrainedSliverCount: constrainedSlivers);
    }

    /// <summary>
    /// One triangle's smallest angle in degrees and its normalized radius ratio
    /// <c>2·inradius/circumradius</c>.
    /// <para>
    /// Angles come from <c>atan2(|u × v|, u · v)</c> on the RAW edge vectors — exact at any
    /// magnitude, with no normalization and no epsilon anywhere (the same choice
    /// <see cref="HoleFiller"/>'s dihedral measure makes). The ratio is written as
    /// <c>8A²/(s·abc)</c> so that it needs one division rather than three: substituting
    /// <c>r = A/s</c> and <c>R = abc/(4A)</c> into <c>2r/R</c> gives it directly, it is
    /// exactly 1 for an equilateral triangle, and it is scale-free by construction — a
    /// dimensionless ratio needs no relative degeneracy floor because there is no absolute
    /// quantity in it to compare.
    /// </para>
    /// <para>
    /// The only guard is an exact-zero one: a side of length zero leaves the angle undefined
    /// and the ratio a 0/0, so such a triangle scores (0, 0) — it has no shape, which is the
    /// honest reading rather than a skipped sample.
    /// </para>
    /// </summary>
    private static (double AngleDegrees, double RadiusRatio) Measure(in Vector3d a, in Vector3d b, in Vector3d c)
    {
        var ab = b - a;
        var bc = c - b;
        var ca = a - c;
        double lab = ab.Length, lbc = bc.Length, lca = ca.Length;
        if (lab == 0 || lbc == 0 || lca == 0)
            return (0, 0);

        double angleA = Angle(ab, -ca);
        double angleB = Angle(bc, -ab);
        double angleC = Angle(ca, -bc);
        double smallest = Math.Min(angleA, Math.Min(angleB, angleC)) * (180.0 / Math.PI);

        double doubleArea = ab.Cross(-ca).Length;
        double semi = 0.5 * (lab + lbc + lca);
        // 2r/R = 2(A/s)/(abc/4A) = 8A^2/(s.abc) = 2.(2A)^2/(s.abc).
        double ratio = 2 * doubleArea * doubleArea / (semi * lab * lbc * lca);
        return (smallest, Math.Min(ratio, 1.0));
    }

    private static double Angle(in Vector3d u, in Vector3d v) => Math.Atan2(u.Cross(v).Length, u.Dot(v));

    private static int AngleBin(double degrees) => degrees switch
    {
        < 5 => 0,
        < 10 => 1,
        < 20 => 2,
        < 30 => 3,
        < 45 => 4,
        _ => 5,
    };

    private static int RatioBin(double ratio)
    {
        for (int i = 0; i < RatioBins.Length; i++)
        {
            if (ratio < RatioBins[i])
                return i;
        }
        return RatioBins.Length;
    }
}

/// <summary>
/// What <see cref="TriangleQuality.Analyze"/> measured. See that class's remarks for what the
/// free/constrained partition can and cannot distinguish.
/// </summary>
/// <param name="TriangleCount">Triangles measured (fanned faces counted individually).</param>
/// <param name="MinAngleDegrees">Smallest angle over the FREE triangles; NaN when every triangle is constrained.</param>
/// <param name="MeanMinAngleDegrees">Mean over free triangles of each one's smallest angle; NaN as above.</param>
/// <param name="MinRadiusRatio">Worst <c>2·inradius/circumradius</c> over the free triangles (1 = equilateral); NaN as above.</param>
/// <param name="MeanRadiusRatio">Mean radius ratio over the free triangles; NaN as above.</param>
/// <param name="SliverCount">Free triangles below <paramref name="SliverAngleDegrees"/>.</param>
/// <param name="WorstTriangle">Face index owning the worst free triangle, or -1 when there are none.</param>
/// <param name="SliverAngleDegrees">The threshold the sliver counts were taken against.</param>
/// <param name="AngleHistogram">Free-triangle minimum angles in bins [0,5) [5,10) [10,20) [20,30) [30,45) [45,60].</param>
/// <param name="RadiusRatioHistogram">Free-triangle radius ratios in bins [0,0.1) [0.1,0.25) [0.25,0.5) [0.5,0.75) [0.75,0.9) [0.9,1].</param>
/// <param name="ConstrainedCount">
/// Triangles every corner of which was pinned — the ones no remeshing operation could have
/// improved, kept out of the figures above rather than assumed good. On a mesh nobody pinned
/// this is 0 and the report is exactly what it would have been without the partition.
/// </param>
/// <param name="MinConstrainedAngleDegrees">Smallest angle among the constrained triangles; NaN when there are none.</param>
/// <param name="ConstrainedSliverCount">Constrained triangles below the sliver angle — reported, not hidden.</param>
public sealed record TriangleQualityReport(
    int TriangleCount,
    double MinAngleDegrees,
    double MeanMinAngleDegrees,
    double MinRadiusRatio,
    double MeanRadiusRatio,
    int SliverCount,
    int WorstTriangle,
    double SliverAngleDegrees,
    IReadOnlyList<int> AngleHistogram,
    IReadOnlyList<int> RadiusRatioHistogram,
    int ConstrainedCount = 0,
    double MinConstrainedAngleDegrees = double.NaN,
    int ConstrainedSliverCount = 0)
{
    /// <summary>Triangles measured by the free-population rules.</summary>
    public int FreeCount => TriangleCount - ConstrainedCount;

    /// <summary>Share of free triangles at or above <see cref="SliverAngleDegrees"/>; NaN when there are none.</summary>
    public double SliverFreeFraction =>
        FreeCount == 0 ? double.NaN : 1.0 - (double)SliverCount / FreeCount;

    /// <summary>An aligned human-readable table, in the style of <c>SceneReport.ToText</c>.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"triangles            {TriangleCount}");
        text.AppendLine($"  free               {FreeCount}");
        if (ConstrainedCount > 0)
            text.AppendLine($"  constrained        {ConstrainedCount} (min angle {MinConstrainedAngleDegrees:F2} deg, {ConstrainedSliverCount} below the bar)");
        text.AppendLine($"min angle            {MinAngleDegrees:F2} deg   (worst face {WorstTriangle})");
        text.AppendLine($"mean min angle       {MeanMinAngleDegrees:F2} deg");
        text.AppendLine($"min radius ratio     {MinRadiusRatio:F4}");
        text.AppendLine($"mean radius ratio    {MeanRadiusRatio:F4}");
        text.AppendLine($"slivers (< {SliverAngleDegrees:F0} deg)     {SliverCount} of {FreeCount} ({1 - SliverFreeFraction:P1})");
        text.AppendLine($"angle bins           [0,5) {AngleHistogram[0]}  [5,10) {AngleHistogram[1]}  [10,20) {AngleHistogram[2]}  [20,30) {AngleHistogram[3]}  [30,45) {AngleHistogram[4]}  [45,60] {AngleHistogram[5]}");
        text.Append($"ratio bins           [0,.1) {RadiusRatioHistogram[0]}  [.1,.25) {RadiusRatioHistogram[1]}  [.25,.5) {RadiusRatioHistogram[2]}  [.5,.75) {RadiusRatioHistogram[3]}  [.75,.9) {RadiusRatioHistogram[4]}  [.9,1] {RadiusRatioHistogram[5]}");
        return text.ToString();
    }
}
