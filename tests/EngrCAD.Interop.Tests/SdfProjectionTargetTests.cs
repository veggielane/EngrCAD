using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class SdfProjectionTargetTests
{
    /// <summary>Worst |d(v)| over a mesh's vertices, measured against the field it should lie on.</summary>
    private static double MaxSurfaceError(HalfEdgeMesh mesh, Sdf field)
    {
        double worst = 0;
        foreach (var vertex in mesh.Vertices)
            worst = Math.Max(worst, Math.Abs(field.Evaluate(vertex.Position)));
        return worst;
    }

    [Fact]
    public void SingleStep_LandsOnAnExactField()
    {
        // A sphere's field is an exact distance, so p - d·grad d is the closest point after
        // ONE step; the residual is the central difference's O(|d|·h²·curvature), not the
        // Newton iteration's.
        var target = new SdfProjectionTarget(Sdf.Sphere(2), iterations: 1);
        foreach (var p in new Vector3d[] { (3, 0, 0), (0, 0.5, 0), (1, 1, 1), (-4, 2, -1) })
        {
            var projected = target.Project(p);
            Assert.Equal(2.0, projected.Length, 9);
            // Radially outward from the centre: the closest point on a sphere.
            Assert.True(projected.Dot(p) > 0);
        }
    }

    [Fact]
    public void Project_NeverCrossesTheSurface()
    {
        // The one-sided guarantee, and the only one there is: every field here is a
        // 1-Lipschitz lower bound, so a step of length |d| cannot reach the surface from
        // either side. The SIGN is therefore preserved no matter how wrong the gradient
        // direction is — which is what makes this safe to iterate without damping.
        var field = Sdf.Box(2, 2, 2) - Sdf.Sphere(1.2); // a difference: a lower bound, not exact
        var target = new SdfProjectionTarget(field, iterations: 6);
        var rng = new Random(20260727);
        for (int i = 0; i < 400; i++)
        {
            var p = new Vector3d(
                rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            double before = field.Evaluate(p);
            if (before == 0)
                continue;
            double after = field.Evaluate(target.Project(p));
            // "Same side, or exactly on it" — and the boundary case of the theorem is
            // reached routinely (a flat face is exact, so one step lands ON the plane), where
            // which side of zero the last bit falls is round-off's business, not the
            // algorithm's. Anything at 1e-12 of a model 4 units across is that case.
            Assert.True(Math.Sign(after) == Math.Sign(before) || Math.Abs(after) < 1e-12,
                $"projection crossed the surface from {p}: {before} -> {after}");
        }
    }

    [Fact]
    public void FictitiousFacesOfADifference_StallRatherThanConverge()
    {
        // The honest limit, pinned so nobody "fixes" the documentation away. Inside the
        // material a subtracted tool removed, the field measures the distance to the TOOL's
        // surface — a face that is not there — and the gradient jumps at the branch switch,
        // so two branches can trade the point back and forth forever. The real surface here
        // is the rim circle where the sphere breaks out of the box's top face.
        var field = Sdf.Box(2, 2, 2) - Sdf.Sphere(1.2);
        var probe = new Vector3d(-0.12869071687045075, 1.1397277597057296, -0.20309810256729754);

        double stalled = field.Evaluate(new SdfProjectionTarget(field, iterations: 6).Project(probe));
        Assert.True(stalled > 0.1, $"the probe should still be off the surface, is {stalled}");

        // The true distance is to the rim at radius sqrt(1.2^2 - 1^2) in the plane y = 1.
        double rimRadius = Math.Sqrt(1.2 * 1.2 - 1);
        double axial = Math.Sqrt(probe.X * probe.X + probe.Z * probe.Z);
        double trueDistance = Math.Sqrt(
            (rimRadius - axial) * (rimRadius - axial) + (probe.Y - 1) * (probe.Y - 1));
        Assert.True(trueDistance > 3 * field.Evaluate(probe),
            "the field is a strict lower bound here, which is the whole cause");
    }

    [Fact]
    public void Iteration_ConvergesOnALowerBoundField()
    {
        // A smooth union is short of the true distance where the blend bulges, so one step
        // under-shoots and iteration is what closes the gap. Measured at the blend, where
        // the deficit is worst.
        var field = Sdf.Sphere(1).Translate((-0.6, 0, 0))
            .SmoothUnion(Sdf.Sphere(1).Translate((0.6, 0, 0)), 0.5);
        var probe = new Vector3d(0, 1.6, 0.4);

        double one = Math.Abs(field.Evaluate(new SdfProjectionTarget(field, 1).Project(probe)));
        double two = Math.Abs(field.Evaluate(new SdfProjectionTarget(field, 2).Project(probe)));
        double four = Math.Abs(field.Evaluate(new SdfProjectionTarget(field, 4).Project(probe)));

        Assert.True(two < one, $"a second step should improve on {one}, got {two}");
        Assert.True(four < two, $"a fourth step should improve on {two}, got {four}");
        Assert.True(four < 1e-3, $"four steps should be near the surface, got {four}");
    }

    [Fact]
    public void GradientStep_IsRelativeToTheFieldsScale()
    {
        // The same shape at 1e-4 scale must project just as well: an absolute difference
        // step would be larger than the whole model (the ladder's rule about scale).
        var field = Sdf.Sphere(1e-4);
        var target = new SdfProjectionTarget(field);
        var projected = target.Project((3e-4, 0, 0));
        Assert.Equal(1e-4, projected.Length, 15);
    }

    [Fact]
    public void UnboundedField_StillProjects()
    {
        // A half-space has infinite bounds, so no scale can be derived; the fallback step is
        // the relative constant itself, which is right for a unit-ish model.
        var field = Sdf.HalfSpace((0, 0, 1), 0.5);
        var projected = new SdfProjectionTarget(field).Project((1, 2, 3));
        Assert.Equal(new Vector3d(1, 2, 0.5), projected, new Vector3dComparer(1e-9));
    }

    [Fact]
    public void Remeshing_SurfaceNetsOutput_DropsTheSurfaceError()
    {
        // The pairing this target exists for: Surface Nets places one vertex per cell by a
        // local fit, so its vertices sit off the true level set by a fraction of the cell.
        // Remeshing against the SOURCE field is the quality-control pass that puts them back.
        var field = Sdf.Sphere(1);
        var mesh = SurfaceNets.Polygonize(field, resolution: 32);
        double before = MaxSurfaceError(mesh, field);

        var options = new RemeshOptions(0.12)
        {
            Iterations = 8,
            FeatureAngleDegrees = 0, // a polygonized sphere's facets meet at large dihedrals
            ProjectionTarget = new SdfProjectionTarget(field),
        };
        var result = Remesher.Remesh(mesh, options);
        result.Mesh.Validate();
        double after = MaxSurfaceError(result.Mesh, field);

        Assert.True(result.Mesh.IsClosed);
        Assert.True(after < before / 10,
            $"remeshing against the field should cut the surface error by an order of magnitude: {before} -> {after}");
        // And the shape survives: the exact sphere's volume, not the polygonization's.
        double exact = 4.0 / 3.0 * Math.PI;
        Assert.True(Math.Abs(result.Mesh.Volume() - exact) / exact < 0.02,
            $"volume {result.Mesh.Volume()} should stay within 2% of {exact}");
    }

    [Fact]
    public void Remeshing_AgainstAMeshSdf_KeepsTheShape()
    {
        // MeshSdf makes any closed mesh a projection target with a sign, which the unsigned
        // MeshProjectionTarget cannot offer; useful when the remesh must not creep inward
        // across a thin wall.
        var source = MeshPrimitives.UvSphere(1, 32, 24);
        var field = new MeshSdf(source);
        var options = new RemeshOptions(0.2)
        {
            Iterations = 10,
            FeatureAngleDegrees = 0,
            ProjectionTarget = new SdfProjectionTarget(field),
        };
        var result = Remesher.Remesh(source, options);
        result.Mesh.Validate();

        Assert.True(result.Mesh.IsClosed);
        Assert.True(Math.Abs(result.Mesh.Volume() - source.Volume()) / source.Volume() < 0.02,
            $"volume {result.Mesh.Volume()} should stay near the source's {source.Volume()}");
    }

    [Fact]
    public void Constructor_RejectsNonsense()
    {
        Assert.Throws<ArgumentNullException>(() => new SdfProjectionTarget(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdfProjectionTarget(Sdf.Sphere(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SdfProjectionTarget(Sdf.Sphere(1), gradientStep: 0));
    }

    private sealed class Vector3dComparer(double tolerance) : IEqualityComparer<Vector3d>
    {
        public bool Equals(Vector3d a, Vector3d b) => (a - b).Length <= tolerance;
        public int GetHashCode(Vector3d v) => 0;
    }
}
