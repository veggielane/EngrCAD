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
    public void DegenerateGrid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SdfContours.OnPlane(
            Sdf.Sphere(1), (0, 0, 0), (1, 0, 0), (0, 1, 0), 1, 8, [0.0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SdfContours.OnPlane(
            Sdf.Sphere(1), (0, 0, 0), (1, 0, 0), (0, 1, 0), 8, 1, [0.0]));
    }

    private static double Radius(in Vector3d p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
}
