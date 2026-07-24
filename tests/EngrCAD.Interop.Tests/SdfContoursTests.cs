using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Marching-squares contour extraction against analytic fields. Tolerances are derived
/// from the discretization: for grid step h, the linear interpolation along a cell edge
/// places the crossing within O(h^2 * field curvature) of the true iso point, so a
/// sphere of radius r bounds the radial error by roughly h^2 / (8 r); asserts use a
/// few times that. Chordal length shortfall per segment is O((h/r)^2), well under 1%.
/// </summary>
public class SdfContoursTests
{
    private const int Samples = 161;             // over a 16-unit span: h = 0.1
    private const double H = 16.0 / (Samples - 1);

    private static IReadOnlyList<SdfContourLevel> SphereSection(
        double radius, double planeZ, params double[] levels) =>
        SdfContours.OnPlane(
            Sdf.Sphere(radius),
            origin: (-8, -8, planeZ), uSide: (16, 0, 0), vSide: (0, 16, 0),
            Samples, Samples, levels);

    [Fact]
    public void SphereEquator_ZeroLevelIsCircleOfSphereRadius()
    {
        var contours = SphereSection(5, 0, 0.0);
        var segments = Assert.Single(contours).Segments;
        Assert.True(segments.Count > 100);

        // h^2 / (8 r) = 2.5e-4; assert 4x that for headroom.
        double tolerance = 4 * H * H / (8 * 5);
        double totalLength = 0;
        foreach (var (a, b) in segments)
        {
            Assert.Equal(5, Radius(a), tolerance);
            Assert.Equal(5, Radius(b), tolerance);
            Assert.True(Math.Abs(a.Z) < 1e-15);   // plane points are built at exactly z = 0
            totalLength += (b - a).Length;
        }
        Assert.Equal(2 * Math.PI * 5, totalLength, 2 * Math.PI * 5 * 0.01);
    }

    [Fact]
    public void SphereCutOffCenter_EachLevelMatchesAnalyticRadius()
    {
        // Plane z = 3 through a radius-5 sphere: field at in-plane radius rho is
        // sqrt(rho^2 + 9) - 5, so level d is the circle rho = sqrt((5 + d)^2 - 9).
        var contours = SphereSection(5, 3, -1.0, 0.0, 1.0);
        Assert.Equal(3, contours.Count);
        foreach (var level in contours)
        {
            double expected = Math.Sqrt(Math.Pow(5 + level.Level, 2) - 9);
            Assert.True(level.Segments.Count > 50);
            // Off-center cut: in-plane curvature is higher than the equator's
            // (kappa up to ~1/2.6 for the innermost ring), so scale the bound.
            double tolerance = 4 * H * H / (8 * Math.Min(expected, 5));
            foreach (var (a, b) in level.Segments)
            {
                Assert.Equal(expected, Radius(a), tolerance);
                Assert.Equal(expected, Radius(b), tolerance);
            }
        }
    }

    [Fact]
    public void SharedEndpoints_AreBitIdentical_SoLoopsCloseExactly()
    {
        // Adjacent cells interpolate a shared edge from the same two samples with the
        // same expression, so every endpoint of a closed contour appears exactly twice
        // under exact equality — the documented chaining contract. The grid is offset
        // by half a cell so no sample lies exactly ON the contour (a node-exact hit is
        // legitimately shared by all four surrounding cells, multiplicity 4).
        var segments = SdfContours.OnPlane(
            Sdf.Sphere(5),
            origin: (-8.05, -8.05, 0), uSide: (16, 0, 0), vSide: (0, 16, 0),
            Samples, Samples, [0.0])[0].Segments;
        var occurrences = new Dictionary<Vector3d, int>();
        foreach (var (a, b) in segments)
        {
            occurrences[a] = occurrences.GetValueOrDefault(a) + 1;
            occurrences[b] = occurrences.GetValueOrDefault(b) + 1;
        }
        Assert.All(occurrences.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public void LevelsThatNeverCross_YieldEmptySegments()
    {
        // Max field value on the grid is at a corner: sqrt(128) - 5 ~ 6.3; min is -5.
        var contours = SphereSection(5, 0, 20.0, -10.0);
        Assert.Empty(contours[0].Segments);
        Assert.Empty(contours[1].Segments);
    }

    [Fact]
    public void ArbitraryPlane_BoxSectionOnYzPlane_HasAnalyticPerimeter()
    {
        // The plane is fully general: sample a box on the x = 0 plane. Box(2, 3, 4)
        // sections to a 3 x 4 rectangle, perimeter 14. Marching squares cuts each of
        // the 4 corners at cell scale, so the perimeter bound is a few h.
        var sdf = Sdf.Box(2, 3, 4);
        var contours = SdfContours.OnPlane(
            sdf, origin: (0, -8, -8), uSide: (0, 16, 0), vSide: (0, 0, 16),
            Samples, Samples, [0.0]);
        var segments = Assert.Single(contours).Segments;
        Assert.True(segments.Count > 50);

        double totalLength = 0;
        foreach (var (a, b) in segments)
        {
            // Every extracted point sits on the iso surface within the sampling error;
            // the box field is piecewise linear so only kink-adjacent edges err, by < h.
            Assert.True(Math.Abs(sdf.Evaluate(a)) <= H);
            Assert.True(Math.Abs(sdf.Evaluate(b)) <= H);
            totalLength += (b - a).Length;
        }
        Assert.Equal(14, totalLength, 5 * H);
    }

    [Fact]
    public void SaddleCell_CenterAverageDecidesTheDiagonalConnection()
    {
        // A hyperbolic section: two spheres centered on diagonally opposite corners of
        // a single sample cell (c0 at (0,0), c2 at (1,1) — the union field's saddle
        // sits at the cell center). With a 2x2 grid the corners alternate
        // inside/outside (mask 5), the ambiguous marching-squares case; the rule under
        // test is the CENTER-AVERAGE disambiguation, avg = (sum of corner values)/4 =
        // 0.5 - r here, so radius decides the topology exactly at r = 0.5.
        static IReadOnlyList<(Vector3d A, Vector3d B)> SaddleSegments(double radius)
        {
            var union = Sdf.Sphere(radius) | Sdf.Sphere(radius).Translate((1, 1, 0));
            var contours = SdfContours.OnPlane(
                union, origin: (0, 0, 0), uSide: (1, 0, 0), vSide: (0, 1, 0), 2, 2, [0.0]);
            return Assert.Single(contours).Segments;
        }

        // Separated spheres (r = 0.4, avg = +0.1): two arcs, each hugging its own
        // inside corner. Crossings are exact from the linear interpolation of the
        // corner values (-r and 1 - r): t = r along each crossing edge.
        var separated = SaddleSegments(0.4);
        Assert.Equal(2, separated.Count);
        Assert.Contains(separated, s => NearCorner(s, 0, 0, 0.4));   // around c0
        Assert.Contains(separated, s => NearCorner(s, 1, 1, 0.4));   // around c2

        // Near-merging spheres (r = 0.65, avg = -0.15): the inside corners connect
        // diagonally — the two segments now isolate the OUTSIDE corners instead, so
        // one segment joins the left edge (x = 0) to the top edge (y = 1) around c3.
        var merged = SaddleSegments(0.65);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, s => Touches(s, p => p.X == 0) && Touches(s, p => p.Y == 1));
        Assert.Contains(merged, s => Touches(s, p => p.Y == 0) && Touches(s, p => p.X == 1));

        // Both endpoints of a segment lie within `reach` (Chebyshev) of the corner —
        // the "arc around one inside corner" shape of the disconnected resolution.
        static bool NearCorner((Vector3d A, Vector3d B) s, double cx, double cy, double reach)
        {
            const double slack = 1e-12;   // exact-arithmetic crossings, roundoff only
            return Chebyshev(s.A, cx, cy) <= reach + slack && Chebyshev(s.B, cx, cy) <= reach + slack;
        }

        static double Chebyshev(in Vector3d p, double cx, double cy) =>
            Math.Max(Math.Abs(p.X - cx), Math.Abs(p.Y - cy));

        // Cell-boundary membership is exact: crossings on the x = 0 edge are built as
        // points[a] + (points[b] - points[a]) * t with identical x coordinates, so the
        // in-plane coordinate is bit-exactly the grid line's.
        static bool Touches((Vector3d A, Vector3d B) s, Func<Vector3d, bool> onEdge) =>
            onEdge(s.A) || onEdge(s.B);
    }

    [Fact]
    public void DegenerateGrid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SdfContours.OnPlane(
            Sdf.Sphere(1), (0, 0, 0), (1, 0, 0), (0, 1, 0), 1, 8, [0.0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SdfContours.OnPlane(
            Sdf.Sphere(1), (0, 0, 0), (1, 0, 0), (0, 1, 0), 8, 1, [0.0]));
    }

    private static double Radius(in Vector3d p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
}
