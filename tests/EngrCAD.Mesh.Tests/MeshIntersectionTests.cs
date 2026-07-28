using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The intersection-segment query layer over <c>Bvh.QueryOverlap</c>: where two surfaces
/// cross, whether they cross at all, and whether a mesh crosses itself. Ground truth is
/// analytic wherever the crossing curve has a closed form.
/// </summary>
public class MeshIntersectionTests
{
    private static HalfEdgeMesh Box(Vector3d min, Vector3d max) =>
        MeshPrimitives.Box(new Aabb(min, max));

    /// <summary>
    /// Two unit boxes overlapping in a corner cross along the boundary of their shared
    /// region — a closed curve of exactly known length: three edges of the overlap box,
    /// twice round (each face of one box that enters the other contributes its own share).
    /// The robust invariants are that the curve is closed and lies on both surfaces, which
    /// is what this asserts.
    /// </summary>
    [Fact]
    public void OverlappingBoxes_CrossAlongAClosedCurveOnBothSurfaces()
    {
        var a = Box((0, 0, 0), (2, 2, 2));
        var b = Box((1, 1, 1), (3, 3, 3));
        var report = MeshIntersection.Between(a, b);

        Assert.True(report.Crosses);
        Assert.NotEmpty(report.Segments);

        // Every endpoint sits on the boundary of the overlap region [1,2]^3: it lies on a
        // face plane of each box, so exactly the coordinates that are shared are pinned.
        foreach (var segment in report.Segments.Where(s => s.Transversal))
        {
            foreach (var p in new[] { segment.Start, segment.End })
            {
                Assert.InRange(p.X, 1 - 1e-9, 2 + 1e-9);
                Assert.InRange(p.Y, 1 - 1e-9, 2 + 1e-9);
                Assert.InRange(p.Z, 1 - 1e-9, 2 + 1e-9);
                // On a face of A (a coordinate equal to 2) AND a face of B (equal to 1).
                Assert.True(
                    Near(p.X, 2) || Near(p.Y, 2) || Near(p.Z, 2), $"{p} is not on a face of A");
                Assert.True(
                    Near(p.X, 1) || Near(p.Y, 1) || Near(p.Z, 1), $"{p} is not on a face of B");
            }
        }

        // The curve is the overlap box's three "inner" edges: 3 x 2 units.
        Assert.Equal(6.0, report.CurveLength, 6);
        Assert.Empty(report.CoplanarOverlaps);

        static bool Near(double value, double target) => Math.Abs(value - target) < 1e-9;
    }

    /// <summary>Disjoint solids produce nothing, and the early-out agrees with the full query.</summary>
    [Fact]
    public void DisjointBoxes_NeitherCrossNorTouch()
    {
        var a = Box((0, 0, 0), (1, 1, 1));
        var b = Box((5, 5, 5), (6, 6, 6));
        var report = MeshIntersection.Between(a, b);

        Assert.False(report.Meets);
        Assert.False(report.Crosses);
        Assert.False(report.Touches);
        Assert.Equal(0.0, report.CurveLength);
        Assert.False(MeshIntersection.Crosses(a, b));
    }

    /// <summary>
    /// Two boxes resting on each other share boundary without interfering, and this is the
    /// distinction the report exists to keep — otherwise every assembly with a seated part
    /// would read as a clash.
    /// <para>Note what a seated part actually produces: flush mating faces (coplanar
    /// overlaps) AND a perfectly good rim of intersection segments, because every side face
    /// of the upper box reaches the lower box's top plane along its bottom edge. The segments
    /// are real; none of them is <c>Transversal</c>, which is the property that decides
    /// interference. A one-sided straddle test would get this wrong, since the lower box's
    /// top face DOES pass clean through each side face's plane.</para>
    /// </summary>
    [Fact]
    public void StackedBoxes_TouchWithoutCrossing()
    {
        var lower = Box((0, 0, 0), (2, 2, 1));
        var upper = Box((0.5, 0.5, 1), (1.5, 1.5, 2));
        var report = MeshIntersection.Between(lower, upper);

        Assert.True(report.Meets);
        Assert.False(report.Crosses);
        Assert.True(report.Touches);
        Assert.Equal(0.0, report.CurveLength);
        Assert.NotEmpty(report.CoplanarOverlaps);
        Assert.NotEmpty(report.Segments);                       // the contact rim exists…
        Assert.All(report.Segments, s => Assert.False(s.Transversal));  // …and none of it penetrates
        Assert.False(MeshIntersection.Crosses(lower, upper));
    }

    /// <summary>
    /// A sphere pierced by a narrower cylinder crosses it in two circles of known radius —
    /// analytic ground truth for the curve's total length, up to the tessellation's chordal
    /// error (the crossing polyline is inscribed, so it comes out SHORT and converges).
    /// </summary>
    [Theory]
    [InlineData(64, 0.03)]
    [InlineData(128, 0.01)]
    public void SpherePiercedByACylinder_MeasuresTheTwoCircles(int segments, double tolerance)
    {
        const double sphereRadius = 1.0;
        const double barRadius = 0.4;
        var sphere = MeshPrimitives.UvSphere(sphereRadius, segments, segments / 2);
        double half = Math.Sqrt(sphereRadius * sphereRadius - barRadius * barRadius) + 0.5;
        var bar = MeshPrimitives.Cylinder(barRadius, 2 * half, segments)
            .Transformed(Matrix4d.CreateTranslation((0, 0, -half)));

        var report = MeshIntersection.Between(sphere, bar);

        Assert.True(report.Crosses);
        double exact = 2 * (2 * Math.PI * barRadius);
        Assert.Equal(exact, report.CurveLength, exact * tolerance);
        Assert.True(report.CurveLength <= exact, "an inscribed crossing polyline cannot be longer");
    }

    /// <summary>A clean primitive crosses nothing of itself; neighbouring faces touch along
    /// their shared edge by construction and must not be reported.</summary>
    [Fact]
    public void AValidMesh_DoesNotSelfIntersect()
    {
        Assert.False(MeshIntersection.WithinItself(MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 3, 5)))).Crosses);
        Assert.False(MeshIntersection.WithinItself(MeshPrimitives.UvSphere(1, 24, 12)).Crosses);
        Assert.False(MeshIntersection.WithinItself(MeshPrimitives.Cylinder(1, 3, 24)).Crosses);
    }

    /// <summary>
    /// …and a mesh that genuinely folds through itself is caught. Two boxes welded into one
    /// mesh while overlapping is the canonical self-intersecting soup: every reported pair
    /// must come from different components, since neither box crosses itself.
    /// </summary>
    [Fact]
    public void OverlappingComponentsInOneMesh_AreReported()
    {
        var a = Box((0, 0, 0), (2, 2, 2));
        var b = Box((1, 1, 1), (3, 3, 3));
        var positions = new List<Vector3d>();
        var faces = new List<IReadOnlyList<int>>();
        foreach (var mesh in new[] { a, b })
        {
            int offset = positions.Count;
            var (points, indexed) = mesh.ToIndexed();
            positions.AddRange(points);
            foreach (var face in indexed)
                faces.Add([.. face.Select(i => i + offset)]);
        }
        var combined = HalfEdgeMesh.Build(positions, faces);

        var report = MeshIntersection.WithinItself(combined);
        Assert.True(report.Crosses);
        Assert.Equal(6.0, report.CurveLength, 6);
        Assert.All(report.Segments, s => Assert.True(s.FaceA < s.FaceB));
    }

    /// <summary>
    /// Scale-free: the guards are relative to the operands' extent, so shrinking the whole
    /// configuration by five decades must not change what is reported. This is the tier that
    /// the BSP boolean got wrong (absolute epsilons applied to areas), so it is worth pinning
    /// on every new query that has degeneracy guards in it.
    /// </summary>
    [Theory]
    [InlineData(1e-5)]
    [InlineData(1.0)]
    [InlineData(1e4)]
    public void TheQueryIsScaleFree(double scale)
    {
        var a = Box((0, 0, 0), (2 * scale, 2 * scale, 2 * scale));
        var b = Box((scale, scale, scale), (3 * scale, 3 * scale, 3 * scale));
        var report = MeshIntersection.Between(a, b);

        Assert.True(report.Crosses);
        Assert.Equal(6.0 * scale, report.CurveLength, 6.0 * scale * 1e-9);
    }

    /// <summary>The early-out must agree with the full query on every case above, since it
    /// is the same narrow phase stopped at the first crossing.</summary>
    [Fact]
    public void TheEarlyOutAgreesWithTheFullQuery()
    {
        var pairs = new[]
        {
            (Box((0, 0, 0), (2, 2, 2)), Box((1, 1, 1), (3, 3, 3))),
            (Box((0, 0, 0), (1, 1, 1)), Box((5, 5, 5), (6, 6, 6))),
            (Box((0, 0, 0), (2, 2, 1)), Box((0.5, 0.5, 1), (1.5, 1.5, 2))),
            (Box((0, 0, 0), (2, 2, 2)), Box((0, 0, 0), (2, 2, 2))),
        };
        foreach (var (a, b) in pairs)
            Assert.Equal(MeshIntersection.Between(a, b).Crosses, MeshIntersection.Crosses(a, b));
    }
}
