using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Regression tests for booleans whose intersection curves are CLOSED CIRCLES interior
/// to faces (a sphere centered inside a box). Before the fix this silently produced
/// wrong geometry: the face-bounds prefilter could not see a pole-bounded hemisphere's
/// dome (edge samples only — the equator is flat), so the cap-plane pairs were skipped;
/// side planes hit the marching tracer, whose region-clipped open polylines stop short
/// of the equator so no face ever split; whole-face probes then misclassified
/// everything (union = just the box, intersection = both shells nested, difference =
/// negative volume — no exception). The fix: surface-domain samples in face bounds,
/// analytic plane × sphere-carrier-revolved circles, closed-interior splitting that
/// honors mandatory seam breaks, band-aware arrangement tracing, and a wrap-split guard
/// so wrapping cuts cannot split contractible fragments.
/// </summary>
public class SphereBooleanTests
{
    private static BrepSolid Box() => SolidFactory.MakeBox(new Aabb((-10, -10, -10), (10, 10, 10)));

    /// <summary>Spherical cap volume V = πh²(3r − h)/3, cap height h.</summary>
    private static double CapVolume(double r, double h) => Math.PI * h * h * (3 * r - h) / 3;

    private static double SphereVolume(double r) => 4.0 / 3 * Math.PI * r * r * r;

    private static double CheckedVolume(BrepSolid result, int genus)
    {
        // Boolean output is topologically sealed: full validation plus Euler–Poincaré.
        result.Validate();
        Assert.True(result.SatisfiesEulerFormula(genus), $"expected genus {genus}");
        var mesh = BRepTessellator.Tessellate(result);
        mesh.Validate();
        Assert.True(mesh.IsClosed, "boolean result must tessellate closed");
        Assert.Equal(2 - 2 * genus, mesh.EulerCharacteristic);
        return mesh.Volume();
    }

    /// <summary>
    /// Tessellation volume tolerance, derived from the discretization: an inscribed
    /// 32-segment sphere already misses ~0.75% of the volume, and the trimmed-face
    /// refinement's monotone-decrease termination rule can leave the worst sliver
    /// chords ~1.5x (or more) the nominal step — sagitta grows with the square of the
    /// chord — so allow 6% of the SPHERICAL portion of the expected volume (box faces
    /// are planar and exact; observed worst case is ~4.5% of the spherical portion).
    /// </summary>
    private static void AssertVolume(double expected, double sphericalPart, double actual) =>
        Assert.True(Math.Abs(actual - expected) <= 0.06 * sphericalPart,
            $"volume {actual} vs expected {expected} (tolerance {0.06 * sphericalPart})");

    // ---- sphere piercing all six faces: every curve is a closed interior circle ----

    private const double R = 13; // caps of height 3 beyond each face plane at ±10

    [Fact]
    public void Union_SpherePiercingAllSixFaces_BoxPlusSixCaps()
    {
        var result = BrepBoolean.Union(Box(), SolidFactory.MakeSphere(R));
        double volume = CheckedVolume(result, genus: 0);

        // The original bug produced exactly the box (the six protruding caps lost).
        Assert.True(volume > 8000, $"union {volume} must exceed the box volume");
        double caps = 6 * CapVolume(R, R - 10);
        AssertVolume(8000 + caps, caps, volume);

        // 6 box faces with circular holes + per hemisphere: polar cap, 4 bulges.
        Assert.Equal(16, result.Faces.Count());
        Assert.DoesNotContain(result.Faces, f => f.IsReversed);
    }

    [Fact]
    public void Intersection_SpherePiercingAllSixFaces_SphereWithCapsCut()
    {
        var result = BrepBoolean.Intersection(Box(), SolidFactory.MakeSphere(R));
        double volume = CheckedVolume(result, genus: 0);

        // The original bug produced the whole sphere nested with the whole box.
        Assert.True(volume < SphereVolume(R), $"intersection {volume} must be less than the sphere volume");
        double expected = SphereVolume(R) - 6 * CapVolume(R, R - 10);
        AssertVolume(expected, expected, volume);

        // 6 planar disks + 2 central hemisphere bands (equator-with-bites ↔ cap ring).
        Assert.Equal(8, result.Faces.Count());
    }

    [Fact]
    public void Difference_SpherePiercingAllSixFaces_CubeFrameGenus5()
    {
        var result = BrepBoolean.Difference(Box(), SolidFactory.MakeSphere(R));

        // 13 < 10·√2 keeps all 12 edge tubes of the box: 8 corner chunks joined by 12
        // tubes is the cube-frame topology, genus E − V + 1 = 12 − 8 + 1 = 5.
        double volume = CheckedVolume(result, genus: 5);

        double removed = SphereVolume(R) - 6 * CapVolume(R, R - 10);
        AssertVolume(8000 - removed, removed, volume);
        Assert.True(volume > 0, "difference volume must be positive");
        Assert.Contains(result.Faces, f => f.IsReversed); // carved sphere walls
    }

    [Fact]
    public void Union_SpherePiercingAllSixFaces_HoleRimsAreExactCircles()
    {
        // The hole rims must be exact analytic circles (never tracer polylines):
        // every rim point lies exactly on the sphere — geometry that must weld is
        // constructed exactly.
        var result = BrepBoolean.Union(Box(), SolidFactory.MakeSphere(R));

        var boxFaces = result.Faces.Where(f => f.Surface is PlaneSurface).ToList();
        Assert.Equal(6, boxFaces.Count);
        foreach (var face in boxFaces)
        {
            Assert.True(face.Loops.Count >= 2, "every box face must have gained a hole loop");
            foreach (var coedge in face.Loops[^1].Coedges)
            {
                for (int i = 0; i <= 8; i++)
                {
                    var p = coedge.Edge.Curve.PointAt(coedge.Edge.Domain.ParameterAt(i / 8.0));
                    Assert.Equal(R, p.DistanceTo(Vector3d.Zero), 9);
                }
            }
        }
    }

    // ---- sphere poking through a single face: one closed circle + one wrap circle ----

    private const double R8 = 8; // center (0,0,6): pokes through z = 10 only, cap height 4

    [Fact]
    public void Union_SphereThroughOneFace_BoxPlusCap()
    {
        var result = BrepBoolean.Union(Box(), SolidFactory.MakeSphere(R8, (0, 0, 6)));
        double volume = CheckedVolume(result, genus: 0);
        double cap = CapVolume(R8, R8 - 4);
        AssertVolume(8000 + cap, cap, volume);
        Assert.Equal(7, result.Faces.Count()); // 6 box faces (top holed) + polar cap
    }

    [Fact]
    public void Intersection_SphereThroughOneFace_SphereMinusCap()
    {
        var result = BrepBoolean.Intersection(Box(), SolidFactory.MakeSphere(R8, (0, 0, 6)));
        double volume = CheckedVolume(result, genus: 0);
        double expected = SphereVolume(R8) - CapVolume(R8, R8 - 4);
        AssertVolume(expected, expected, volume);
    }

    [Fact]
    public void Difference_SphereThroughOneFace_BoxWithDimple()
    {
        var result = BrepBoolean.Difference(Box(), SolidFactory.MakeSphere(R8, (0, 0, 6)));
        double volume = CheckedVolume(result, genus: 0);
        double removed = SphereVolume(R8) - CapVolume(R8, R8 - 4);
        AssertVolume(8000 - removed, removed, volume);
        Assert.Contains(result.Faces, f => f.IsReversed);
    }
}
