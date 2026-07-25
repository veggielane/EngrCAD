using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Lofted solids through the tessellator: closure/manifoldness (the weld invariants) and
/// volumes against analytic ground truth.
/// </summary>
public class LoftTessellationTests
{
    private static Profile Square(double half, double z) => Profile.FromPoints(
        [(-half, -half, z), (half, -half, z), (half, half, z), (-half, half, z)]);

    private static Profile Circle(double radius, double z) =>
        Profile.Circle((0, 0, z), Vector3d.UnitX, Vector3d.UnitY, radius);

    /// <summary>Exact volume of a frustum between parallel faces of areas a and b.</summary>
    private static double Frustum(double a, double b, double height) =>
        height / 3 * (a + b + Math.Sqrt(a * b));

    [Fact]
    public void Loft_BetweenEqualSquares_IsExactlyThePrism()
    {
        var mesh = BRepTessellator.Tessellate(SolidFactory.Loft(Square(1, 0), Square(1, 2)));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        Assert.Equal(8.0, mesh.Volume(), 9); // 2 x 2 x 2, planar faces tessellate exactly
    }

    [Fact]
    public void Loft_SquareToSmallerSquare_HasTheExactFrustumVolume()
    {
        // Every lateral strip is a planar trapezoid (the two section edges are parallel),
        // so the mesh volume IS the prismatoid volume, with no discretization error.
        var mesh = BRepTessellator.Tessellate(SolidFactory.Loft(Square(1, 0), Square(0.5, 3)));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(Frustum(2 * 2, 1 * 1, 3), mesh.Volume(), 9);
    }

    [Fact]
    public void Loft_BetweenTwoCircles_MatchesTheConeFrustumTessellation()
    {
        // A ruled loft between coaxial circles IS a cone frustum; both tessellate to the
        // same n-gonal frustum, so their volumes must agree to weld precision.
        const int n = 48;
        var loft = BRepTessellator.Tessellate(SolidFactory.Loft(Circle(2, 0), Circle(1, 3)), segmentsPerCircle: n);
        var cone = BRepTessellator.Tessellate(SolidFactory.MakeCone(2, 1, 3), segmentsPerCircle: n);
        loft.Validate();
        Assert.True(loft.IsClosed);
        Assert.Equal(cone.Volume(), loft.Volume(), 9);

        // ... and both approach the analytic frustum as the polygon inscribes the circle.
        double inscribed = n / (2 * Math.PI) * Math.Sin(2 * Math.PI / n);
        Assert.Equal(inscribed * Frustum(Math.PI * 4, Math.PI * 1, 3), loft.Volume(), 9);
    }

    [Fact]
    public void Loft_BetweenEqualCircles_MatchesTheCylinderTessellation()
    {
        const int n = 32;
        var loft = BRepTessellator.Tessellate(SolidFactory.Loft(Circle(1.5, 0), Circle(1.5, 4)), segmentsPerCircle: n);
        var cylinder = BRepTessellator.Tessellate(SolidFactory.MakeCylinder(1.5, 4), segmentsPerCircle: n);
        loft.Validate();
        Assert.True(loft.IsClosed);
        Assert.Equal(cylinder.Volume(), loft.Volume(), 9);
    }

    [Fact]
    public void SmoothLoft_ThroughThreeSquares_HasTheExactFrustumStackVolume()
    {
        // The mesh is exactly a stack of square frusta sampled from the quadratic blend:
        // corners are straight sections (2 grid columns) and the lateral quads are planar,
        // so the volume can be predicted in closed form from the surface itself.
        var solid = SolidFactory.Loft([Square(1, 0), Square(2, 1), Square(0.5, 3)]);
        const int curveSamples = 24;
        var mesh = BRepTessellator.Tessellate(solid, curveSamples: curveSamples);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);

        var surface = (LoftedSurface)solid.Faces.First(f => f.Surface is LoftedSurface).Surface;
        double expected = 0;
        var previous = surface.PointAt(0, 0);
        for (int j = 1; j <= curveSamples; j++)
        {
            var current = surface.PointAt(0, (double)j / curveSamples);
            expected += Frustum(
                4 * previous.X * previous.X, 4 * current.X * current.X, Math.Abs(current.Z - previous.Z));
            previous = current;
        }
        Assert.Equal(expected, mesh.Volume(), 9);
    }

    [Fact]
    public void RuledLoft_ThroughThreeCircles_IsTheStackOfConeFrusta()
    {
        const int n = 64;
        var solid = SolidFactory.Loft([Circle(2, 0), Circle(1.2, 1), Circle(1.7, 2.5)], LoftStyle.Ruled);
        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: n);
        mesh.Validate();
        Assert.True(mesh.IsClosed);

        double inscribed = n / (2 * Math.PI) * Math.Sin(2 * Math.PI / n);
        double expected = inscribed * (
            Frustum(Math.PI * 4, Math.PI * 1.44, 1) +
            Frustum(Math.PI * 1.44, Math.PI * 2.89, 1.5));
        Assert.Equal(expected, mesh.Volume(), 9);
    }

    [Fact]
    public void SmoothLoft_ThroughThreeCircles_IsClosedAndManifold()
    {
        var solid = SolidFactory.Loft([Circle(2, 0), Circle(1.2, 1), Circle(1.7, 2.5)]);
        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle: 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
        // A barrel: bounded below by the smallest cone stack and above by the largest.
        Assert.InRange(mesh.Volume(), 10.0, 20.0);
    }

    [Fact]
    public void Loft_MixedStraightAndCurvedSections_WeldsWithoutCracks()
    {
        // The sampling-unification case: a square skinned to a circle of arcs. Straight
        // sections are promoted to degree-1 NURBS so grid and edge densities agree.
        var arcs = new Curve3d[4];
        for (int i = 0; i < 4; i++)
        {
            double start = i * Math.PI / 2;
            arcs[i] = NurbsCurve.Arc((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY, 1.2, start, start + Math.PI / 2);
        }
        var mesh = BRepTessellator.Tessellate(SolidFactory.Loft(Square(1, 0), new Profile(arcs)));
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.Equal(2, mesh.EulerCharacteristic);
    }

    [Fact]
    public void Loft_TwistAlignedSections_WeldAndKeepTheirVolume()
    {
        // Sections listed from different corners and wound oppositely: alignment must
        // recover the untwisted prism exactly.
        var rotated = Profile.FromPoints([(1, -1, 2), (1, 1, 2), (-1, 1, 2), (-1, -1, 2)]);
        var reversed = Profile.FromPoints([(-1, -1, 2), (-1, 1, 2), (1, 1, 2), (1, -1, 2)]);
        foreach (var top in new[] { rotated, reversed })
        {
            var mesh = BRepTessellator.Tessellate(SolidFactory.Loft(Square(1, 0), top));
            mesh.Validate();
            Assert.True(mesh.IsClosed);
            Assert.Equal(8.0, mesh.Volume(), 9);
        }
    }
}
