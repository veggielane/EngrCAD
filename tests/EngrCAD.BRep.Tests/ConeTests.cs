using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Topology and geometry of <see cref="SolidFactory.MakeCone"/> (tessellated volumes
/// live in Interop.Tests, Shape wiring in Modeling.Tests).
/// </summary>
public class ConeTests
{
    [Fact]
    public void Frustum_ValidatesWithCylinderLikeTopology()
    {
        var cone = SolidFactory.MakeCone(2, 1, 3);
        cone.Validate();
        Assert.True(cone.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(3, cone.Faces.Count());
        Assert.Equal(2, cone.Edges.Count());
        Assert.Equal(2, cone.Vertices.Count());

        var side = cone.Faces.Single(f => f.Surface is RevolvedSurface);
        Assert.Equal(2, side.Loops.Count);
    }

    [Fact]
    public void ApexCone_TopRimBecomesAPoleWithNoEdgeOrCap()
    {
        var cone = SolidFactory.MakeCone(1.5, 0, 2);
        cone.Validate();
        Assert.True(cone.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(2, cone.Faces.Count());   // side + bottom cap only
        Assert.Single(cone.Edges);
        Assert.Single(cone.Vertices);

        // The generator's top end sits exactly on the axis: the pole ring is a point.
        var side = (RevolvedSurface)cone.Faces.Single(f => f.Surface is RevolvedSurface).Surface;
        var apex = side.PointAt(0, side.DomainV.End);
        Assert.True(apex.AreEqual(new Vector3d(0, 0, 2), Tolerance.Default));
        Assert.True(side.PointAt(2, side.DomainV.End).AreEqual(apex, Tolerance.Default));
    }

    [Fact]
    public void ApexAtBottom_IsSupportedToo()
    {
        var cone = SolidFactory.MakeCone(0, 1, 1);
        cone.Validate();
        Assert.True(cone.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(2, cone.Faces.Count());   // side + top cap only
        Assert.Single(cone.Edges);
    }

    [Fact]
    public void RimCircles_ArePhaseAlignedWithTheSurfaceSeam()
    {
        // Weld invariant: rim circle samples must coincide with the revolved surface's
        // samples at the same angles (u = 0 alignment; never an arbitrary frame).
        var cone = SolidFactory.MakeCone(2, 1, 3, (5, -2, 1), (1, 1, 2));
        cone.Validate();
        var side = cone.Faces.Single(f => f.Surface is RevolvedSurface);
        var surface = (RevolvedSurface)side.Surface;

        foreach (var loop in side.Loops)
        {
            var edge = Assert.Single(loop.Coedges).Edge;
            var circle = Assert.IsType<Circle3d>(edge.Curve);
            double v = circle.Radius > 1.5 ? surface.DomainV.Start : surface.DomainV.End;
            for (int i = 0; i <= 16; i++)
            {
                double u = 2 * Math.PI * i / 16;
                Assert.True(circle.PointAt(u).AreEqual(surface.PointAt(u, v), Tolerance.Default),
                    $"rim sample at u={u} is off the surface ring");
            }
        }
    }

    [Fact]
    public void DegenerateInputs_Throw()
    {
        Assert.Throws<ArgumentException>(() => SolidFactory.MakeCone(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidFactory.MakeCone(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidFactory.MakeCone(1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SolidFactory.MakeCone(1, 1, 0));
    }

    [Fact]
    public void StepRoundTrip_FrustumSurvivesAsSurfaceOfRevolution()
    {
        var original = SolidFactory.MakeCone(2, 1, 3);
        var result = StepReader.Read(StepWriter.Write(original, "cone"));
        Assert.Empty(result.Diagnostics);
        var read = Assert.Single(result.Solids);

        read.Validate();
        Assert.True(read.SatisfiesEulerFormula(genus: 0));
        Assert.Equal(original.Faces.Count(), read.Faces.Count());
        Assert.Equal(original.Edges.Count(), read.Edges.Count());

        // Sampled side geometry survives exactly (generator re-trimmed from the rims).
        var originalSide = (RevolvedSurface)original.Faces.Single(f => f.Surface is RevolvedSurface).Surface;
        var readSide = (RevolvedSurface)read.Faces.Single(f => f.Surface is RevolvedSurface).Surface;
        for (int i = 0; i <= 8; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                double u = originalSide.DomainU.ParameterAt(i / 8.0);
                double v0 = originalSide.DomainV.ParameterAt(j / 4.0);
                double v1 = readSide.DomainV.ParameterAt(j / 4.0);
                Assert.True(
                    originalSide.PointAt(u, v0).AreEqual(readSide.PointAt(u, v1), new Tolerance(1e-6, 1e-6)),
                    $"surface sample ({i},{j}) drifted through STEP");
            }
        }
    }
}
