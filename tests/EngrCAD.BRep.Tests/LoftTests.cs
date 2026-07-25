using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Topology, exactness and alignment of <see cref="SolidFactory.Loft"/> and
/// <see cref="LoftedSurface"/>. Tessellated volumes live in Interop.Tests.
/// </summary>
public class LoftTests
{
    private static Profile Square(double half, double z) => Profile.FromPoints(
        [(-half, -half, z), (half, -half, z), (half, half, z), (-half, half, z)]);

    private static Profile Circle(double radius, double z) =>
        Profile.Circle((0, 0, z), Vector3d.UnitX, Vector3d.UnitY, radius);

    [Fact]
    public void Loft_TwoSquares_HasBoxTopology()
    {
        var solid = SolidFactory.Loft(Square(1, 0), Square(1, 2));
        solid.Validate();
        Assert.Equal(8, solid.Vertices.Count());
        Assert.Equal(12, solid.Edges.Count());
        Assert.Equal(6, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(4, solid.Faces.Count(f => f.Surface is LoftedSurface));
        Assert.Equal(2, solid.Faces.Count(f => f.Surface is PlaneSurface));
    }

    [Fact]
    public void Loft_BetweenTranslatedCopies_IsExactlyTheExtrusion()
    {
        // The defining sanity check: a loft between a section and its translate must
        // reproduce the extrusion's ruled surface point for point.
        var solid = SolidFactory.Loft(Square(1, 0), Square(1, 2));
        foreach (var face in solid.Faces.Where(f => f.Surface is LoftedSurface))
        {
            var surface = (LoftedSurface)face.Surface;
            for (int i = 0; i <= 8; i++)
            {
                for (int j = 0; j <= 8; j++)
                {
                    double u = i / 8.0, v = j / 8.0;
                    var start = surface.Sections[0];
                    var expected = start.PointAt(start.Domain.ParameterAt(u)) + new Vector3d(0, 0, 2 * v);
                    Assert.True(surface.PointAt(u, v).AreEqual(expected, Tolerance.Default));
                }
            }
        }
    }

    [Fact]
    public void Loft_TwoEqualCircles_IsExactlyACylinder()
    {
        var solid = SolidFactory.Loft(Circle(1.5, 0), Circle(1.5, 4));
        solid.Validate();
        // Cylinder topology: one band with two rim loops plus two caps.
        Assert.Equal(2, solid.Vertices.Count());
        Assert.Equal(2, solid.Edges.Count());
        Assert.Equal(3, solid.Faces.Count());
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        var band = (LoftedSurface)solid.Faces.Single(f => f.Surface is LoftedSurface).Surface;
        Assert.True(band.IsClosedU);
        for (int i = 0; i <= 16; i++)
        {
            for (int j = 0; j <= 8; j++)
            {
                var p = band.PointAt(i / 16.0, j / 8.0);
                Assert.Equal(1.5, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12);
                Assert.Equal(4 * (j / 8.0), p.Z, 12);
            }
        }
    }

    [Fact]
    public void Loft_TwoCircles_OfDifferentRadius_IsExactlyAConeFrustum()
    {
        var solid = SolidFactory.Loft(Circle(2, 0), Circle(1, 3));
        solid.Validate();
        var band = (LoftedSurface)solid.Faces.Single(f => f.Surface is LoftedSurface).Surface;
        for (int j = 0; j <= 8; j++)
        {
            double v = j / 8.0;
            double expected = 2 + (1 - 2) * v;
            for (int i = 0; i <= 16; i++)
            {
                var p = band.PointAt(i / 16.0, v);
                Assert.Equal(expected, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12);
                Assert.Equal(3 * v, p.Z, 12);
            }
        }
    }

    [Fact]
    public void SmoothLoft_PassesExactlyThroughEveryIntermediateSection()
    {
        // Three unequal sections: the interpolating blend must reproduce each section
        // curve at its own v parameter, bit for bit (the tessellation grid's boundary
        // rows are exactly these, and they must weld to the shared section edges).
        var solid = SolidFactory.Loft([Square(1, 0), Square(2, 1), Square(0.5, 3)]);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        // Smooth: intermediate sections leave no edge, so the topology is still a box.
        Assert.Equal(8, solid.Vertices.Count());
        Assert.Equal(6, solid.Faces.Count());

        foreach (var face in solid.Faces.Where(f => f.Surface is LoftedSurface))
        {
            var surface = (LoftedSurface)face.Surface;
            Assert.Equal(3, surface.Sections.Count);
            Assert.Equal(2, surface.Degree);
            for (int k = 0; k < surface.Sections.Count; k++)
            {
                var section = surface.Sections[k];
                for (int i = 0; i <= 4; i++)
                {
                    double u = i / 4.0;
                    var expected = section.PointAt(section.Domain.ParameterAt(u));
                    var actual = surface.PointAt(u, surface.SectionParameters[k]);
                    // Exact equality on purpose: this is a weld invariant, not a
                    // measurement — the boundary row IS the section curve's samples.
                    Assert.Equal(expected.X, actual.X);
                    Assert.Equal(expected.Y, actual.Y);
                    Assert.Equal(expected.Z, actual.Z);
                }
            }
        }
    }

    [Fact]
    public void RuledLoft_MakesOneBandPerInterval()
    {
        var solid = SolidFactory.Loft([Square(1, 0), Square(2, 1), Square(0.5, 3)], LoftStyle.Ruled);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(12, solid.Vertices.Count());   // 3 sections x 4 corners
        Assert.Equal(20, solid.Edges.Count());      // 12 section edges + 8 rails
        Assert.Equal(10, solid.Faces.Count());      // 8 strips + 2 caps
        Assert.Equal(8, solid.Faces.Count(f => f.Surface is LoftedSurface));
        Assert.All(
            solid.Faces.Where(f => f.Surface is LoftedSurface),
            f => Assert.Equal(1, ((LoftedSurface)f.Surface).Degree));
    }

    [Fact]
    public void Loft_ReversedSection_IsAutoWound()
    {
        var clockwise = Profile.FromPoints([(-1, -1, 2), (-1, 1, 2), (1, 1, 2), (1, -1, 2)]);
        Assert.True(clockwise.Normal.AreEqual(-Vector3d.UnitZ, Tolerance.Default));
        var solid = SolidFactory.Loft(Square(1, 0), clockwise);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        // Outward convention: every lateral surface's ∂u x ∂v points away from the axis.
        foreach (var face in solid.Faces.Where(f => f.Surface is LoftedSurface))
        {
            var surface = (LoftedSurface)face.Surface;
            var point = surface.PointAt(0.5, 0.5);
            var normal = surface.NormalAt(0.5, 0.5);
            Assert.True(normal.Dot(new Vector3d(point.X, point.Y, 0).Normalized()) > 0.99);
        }
    }

    [Fact]
    public void Loft_RotatedSegmentOrder_IsAlignedToLeastTwist()
    {
        // The same square, but listed starting from a different corner: without alignment
        // the skin would twist a quarter turn.
        var twisted = Profile.FromPoints([(1, -1, 2), (1, 1, 2), (-1, 1, 2), (-1, -1, 2)]);
        var solid = SolidFactory.Loft(Square(1, 0), twisted);
        solid.Validate();

        foreach (var face in solid.Faces.Where(f => f.Surface is LoftedSurface))
        {
            var surface = (LoftedSurface)face.Surface;
            for (int i = 0; i <= 4; i++)
            {
                var bottom = surface.PointAt(i / 4.0, 0);
                var top = surface.PointAt(i / 4.0, 1);
                Assert.Equal(bottom.X, top.X, 12);
                Assert.Equal(bottom.Y, top.Y, 12);
            }
        }
    }

    [Fact]
    public void Loft_PhaseShiftedClosedSection_IsAlignedToLeastTwist()
    {
        // The same circle with its parameterization rotated 0.7 rad: the seam shift must
        // undo it, or the cylinder would come out as a twisted ribbon.
        const double angle = 0.7;
        var rotated = new Profile([new Circle3d(
            (0, 0, 3),
            (Math.Cos(angle), Math.Sin(angle), 0),
            (-Math.Sin(angle), Math.Cos(angle), 0),
            1.5)]);
        var solid = SolidFactory.Loft(Circle(1.5, 0), rotated);
        solid.Validate();

        var band = (LoftedSurface)solid.Faces.Single(f => f.Surface is LoftedSurface).Surface;
        Assert.IsType<PhaseShiftedCurve>(band.Sections[1]);
        for (int i = 0; i < 16; i++)
        {
            var bottom = band.PointAt(i / 16.0, 0);
            var top = band.PointAt(i / 16.0, 1);
            Assert.Equal(bottom.X, top.X, 9);
            Assert.Equal(bottom.Y, top.Y, 9);
        }
    }

    [Fact]
    public void LoftedSurface_DerivativesAreExact()
    {
        // Analytic ∂u/∂v against central differences of the surface itself.
        var solid = SolidFactory.Loft([Circle(2, 0), Circle(1.2, 1), Circle(1.7, 2.5)]);
        var band = (LoftedSurface)solid.Faces.Single(f => f.Surface is LoftedSurface).Surface;
        const double h = 1e-6;
        for (int i = 1; i < 8; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                double u = i / 8.0, v = j / 8.0;
                var du = (band.PointAt(u + h, v) - band.PointAt(u - h, v)) / (2 * h);
                var dv = (band.PointAt(u, v + h) - band.PointAt(u, v - h)) / (2 * h);
                Assert.True((band.DerivativeU(u, v) - du).Length < 1e-6 * Math.Max(1, du.Length));
                Assert.True((band.DerivativeV(u, v) - dv).Length < 1e-6 * Math.Max(1, dv.Length));
                Assert.True(band.NormalAt(u, v).AreEqual(du.Cross(dv).Normalized(), new Tolerance(1e-7, 1e-7)));
            }
        }
    }

    [Fact]
    public void Loft_MixedStraightAndCurvedSections_UnifiesTheSampling()
    {
        // A square lofted to a rounded section: the straight sides must be re-expressed as
        // NURBS so both sides of every strip sample at the same density (the weld rule).
        var arcs = new Curve3d[4];
        var corners = new[] { 0.0, Math.PI / 2, Math.PI, 3 * Math.PI / 2 };
        for (int i = 0; i < 4; i++)
        {
            arcs[i] = NurbsCurve.Arc(
                (0, 0, 2), Vector3d.UnitX, Vector3d.UnitY, 1.2, corners[i], corners[i] + Math.PI / 2);
        }
        var solid = SolidFactory.Loft(Square(1, 0), new Profile(arcs));
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));

        foreach (var face in solid.Faces.Where(f => f.Surface is LoftedSurface))
        {
            var surface = (LoftedSurface)face.Surface;
            Assert.All(surface.Sections, s => Assert.IsNotType<Line3d>(s.Underlying));
            // Straight sections stay geometrically straight: the promotion is exact.
            var section = surface.Sections[0];
            var a = section.PointAt(section.Domain.Start);
            var b = section.PointAt(section.Domain.End);
            var mid = section.PointAt(section.Domain.Mid);
            Assert.True(mid.AreEqual((a + b) * 0.5, Tolerance.Default));
        }
    }

    [Fact]
    public void Loft_Validations()
    {
        Assert.Throws<ArgumentException>(() => SolidFactory.Loft([Square(1, 0)]));
        // Mismatched segment counts.
        Assert.Throws<ArgumentException>(() => SolidFactory.Loft(
            Square(1, 0), Profile.FromPoints([(-1, -1, 2), (1, -1, 2), (0, 1, 2)])));
        // Chain against single closed curve.
        Assert.Throws<ArgumentException>(() => SolidFactory.Loft(Square(1, 0), Circle(1, 2)));
        // Coincident sections.
        Assert.Throws<ArgumentException>(() => SolidFactory.Loft(Square(1, 0), Square(1, 0)));
        // Section plane containing the travel direction.
        var edgeOn = Profile.FromPoints([(0, -1, 0), (0, 1, 0), (0, 1, 2), (0, -1, 2)]);
        Assert.Throws<ArgumentException>(() => SolidFactory.Loft(Square(1, 0), edgeOn));
    }

    [Fact]
    public void LoftedSurface_RejectsIncompatibleSectionInputs()
    {
        var line = new Line3d((0, 0, 0), (1, 0, 0));
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1);
        Assert.Throws<ArgumentException>(() => new LoftedSurface([line]));
        Assert.Throws<ArgumentException>(() => new LoftedSurface([line, circle]));
        Assert.Throws<ArgumentException>(() => new LoftedSurface(
            [line, new Line3d((0, 0, 1), (1, 0, 1))], [0.0, 0.5]));
        Assert.Throws<ArgumentException>(() => new LoftedSurface(
            [line, new Line3d((0, 0, 1), (1, 0, 1))], [0.0, 1.0, 2.0]));
    }

    [Fact]
    public void PhaseShiftedCurve_MovesTheSeamWithoutChangingTheCurve()
    {
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 2);
        var shifted = new PhaseShiftedCurve(circle, Math.PI / 2);
        Assert.True(shifted.IsClosed);
        Assert.Same(circle, shifted.Underlying);
        Assert.True(shifted.PointAt(0).AreEqual(circle.PointAt(Math.PI / 2), Tolerance.Default));
        // Wraps rather than clamps: a parameter past the seam re-enters at the start.
        Assert.True(shifted.PointAt(1.7 * Math.PI).AreEqual(
            circle.PointAt(1.7 * Math.PI + Math.PI / 2 - 2 * Math.PI), Tolerance.Default));
        Assert.Throws<ArgumentException>(() => new PhaseShiftedCurve(new Line3d((0, 0, 0), (1, 0, 0)), 0.1));
    }
}
