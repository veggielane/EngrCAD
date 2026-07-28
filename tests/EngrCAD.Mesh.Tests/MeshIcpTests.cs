using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshIcpTests
{
    /// <summary>An asymmetric target with no continuous symmetry (5 × 3 × 2 ellipsoid).</summary>
    private static HalfEdgeMesh Ellipsoid() =>
        MeshPrimitives.UvSphere(1, 48, 24).Transformed(Matrix4d.CreateScale(new Vector3d(5, 3, 2)));

    private static Matrix4d SmallRigid() =>
        Matrix4d.CreateTranslation(new Vector3d(0.2, -0.15, 0.1))
        * Matrix4d.CreateFromAxisAngle(new Vector3d(1, 2, 0.5).Normalized(), 5 * Math.PI / 180);

    [Fact]
    public void RecoversAKnownRigidTransform()
    {
        var target = Ellipsoid();
        var displaced = SmallRigid();
        var source = target.Vertices.Select(v => displaced.TransformPoint(v.Position)).ToArray();

        var result = MeshIcp.Align(source, target, new IcpOptions { ConvergenceTolerance = 1e-12 });

        Assert.True(result.Converged, $"ICP did not converge after {result.Iterations} iterations, rms {result.RmsError:E3}.");
        // Aligning the displaced points must reproduce the original vertices: the
        // composition (align ∘ displaced) is the identity on every vertex.
        double worst = 0;
        foreach (var v in target.Vertices)
        {
            var roundTripped = result.Transform.TransformPoint(displaced.TransformPoint(v.Position));
            worst = Math.Max(worst, (roundTripped - v.Position).Length);
        }
        Assert.True(worst < 1e-6, $"Worst round-trip deviation {worst:E3}");
        Assert.True(result.RmsError < 1e-9, $"Final rms {result.RmsError:E3}");
    }

    [Fact]
    public void MeshOverload_AlignsAMeshToAMesh()
    {
        var target = Ellipsoid();
        var displaced = SmallRigid();
        var source = target.Transformed(displaced);

        var result = MeshIcp.Align(source, target);
        Assert.True(result.Converged);
        Assert.True(result.RmsError < 1e-8);
    }

    [Fact]
    public void PartialOverlap_TopHalfStillAligns()
    {
        var target = Ellipsoid();
        var displaced = SmallRigid();
        var source = target.Vertices
            .Where(v => v.Position.Z > 0.2)
            .Select(v => displaced.TransformPoint(v.Position))
            .ToArray();
        Assert.True(source.Length > 100);

        var result = MeshIcp.Align(source, target, new IcpOptions { ConvergenceTolerance = 1e-12 });
        Assert.True(result.Converged);
        Assert.True(result.RmsError < 1e-7, $"rms {result.RmsError:E3}");
    }

    [Fact]
    public void OutlierRejection_IgnoresFarPoints()
    {
        var target = Ellipsoid();
        var displaced = SmallRigid();
        var source = target.Vertices.Select(v => displaced.TransformPoint(v.Position)).ToList();
        // A few gross outliers, far outside any correspondence radius.
        source.Add(new Vector3d(40, 0, 0));
        source.Add(new Vector3d(0, 45, 10));
        source.Add(new Vector3d(-30, -30, -30));

        var result = MeshIcp.Align(source, target, new IcpOptions
        {
            MaxCorrespondenceDistance = 2.0,
            ConvergenceTolerance = 1e-12,
        });
        Assert.True(result.Converged);
        Assert.True(result.RmsError < 1e-8, $"rms with outliers {result.RmsError:E3}");
        Assert.Equal(target.VertexCount, result.Correspondences); // outliers not counted
    }

    [Fact]
    public void AlreadyAligned_ConvergesImmediatelyToIdentity()
    {
        var target = Ellipsoid();
        var source = target.Vertices.Select(v => v.Position).ToArray();
        var result = MeshIcp.Align(source, target);
        Assert.True(result.Converged);
        Assert.Equal(1, result.Iterations);
        Assert.Equal(Matrix4d.Identity, result.Transform);
        Assert.True(result.RmsError < 1e-12);
    }

    [Fact]
    public void PlanarCorrespondences_RefuseInsteadOfRegularizing()
    {
        // Every correspondence lies on one plane: two translations and the in-plane
        // rotation are unconstrained, the 6x6 goes singular, and the honest answer is
        // Converged = false - never a Tikhonov-damped arbitrary minimum.
        var plane = LaplacianSmootherTests.PlaneGrid(10);
        var source = new List<Vector3d>();
        for (int i = 2; i <= 8; i++)
        {
            for (int j = 2; j <= 8; j++)
                source.Add(new Vector3d(i + 0.25, j + 0.25, 0.4)); // hovering above the grid
        }

        var result = MeshIcp.Align(source, plane, new IcpOptions { MaxIterations = 5 });
        Assert.False(result.Converged);
    }

    [Fact]
    public void Align_IsDeterministic()
    {
        var target = Ellipsoid();
        var displaced = SmallRigid();
        var source = target.Vertices.Select(v => displaced.TransformPoint(v.Position)).ToArray();

        var a = MeshIcp.Align(source, target);
        var b = MeshIcp.Align(source, target);
        Assert.Equal(a.Transform, b.Transform); // bitwise (Matrix4d.Equals is bitwise)
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.RmsError), BitConverter.DoubleToInt64Bits(b.RmsError));
    }

    [Fact]
    public void Align_RejectsBadInput()
    {
        var target = Ellipsoid();
        Assert.Throws<ArgumentException>(() => MeshIcp.Align(Array.Empty<Vector3d>(), target));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshIcp.Align(
            new[] { Vector3d.Zero }, target, new IcpOptions { MaxIterations = 0 }));
    }
}

public class SymmetricEigen3Tests
{
    [Fact]
    public void BothOrderings_AgreeAndAreSorted()
    {
        // A symmetric matrix with known eigenvalues: diag(1, 2, 3) rotated.
        var (descValues, descVectors) = SymmetricEigen3.SolveDescending(2.5, 0.5, 0.25, 2.0, -0.3, 1.5);
        var (ascValues, ascVectors) = SymmetricEigen3.SolveAscending(2.5, 0.5, 0.25, 2.0, -0.3, 1.5);

        Assert.True(descValues[0] >= descValues[1] && descValues[1] >= descValues[2]);
        Assert.True(ascValues[0] <= ascValues[1] && ascValues[1] <= ascValues[2]);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(descValues[i], ascValues[2 - i], 12);
            // Same eigenvector up to sign.
            double dot = Math.Abs(descVectors[i].Dot(ascVectors[2 - i]));
            Assert.Equal(1.0, dot, 10);
        }

        // Trace and orthonormality.
        Assert.Equal(2.5 + 2.0 + 1.5, descValues[0] + descValues[1] + descValues[2], 10);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(1.0, descVectors[i].Length, 10);
            Assert.Equal(0.0, descVectors[i].Dot(descVectors[(i + 1) % 3]), 10);
        }
    }

    [Fact]
    public void PrincipalInertia_StillWorksThroughCoreEigen()
    {
        // A box's principal axes are the coordinate axes and the moments are analytic:
        // regression cover for MassProperties.Principal after the duplicate Jacobi
        // solver's deletion.
        var box = MeshPrimitives.Box(4, 2, 1);
        var principal = box.MassProperties().Principal();
        double volume = 8;
        // I = m/12 * (b^2 + c^2) about each axis, ascending.
        var expected = new[] { volume / 12 * (2 * 2 + 1), volume / 12 * (4 * 4 + 1), volume / 12 * (4 * 4 + 2 * 2) };
        Assert.Equal(expected[0], principal.Moments.X, 9);
        Assert.Equal(expected[1], principal.Moments.Y, 9);
        Assert.Equal(expected[2], principal.Moments.Z, 9);
    }
}
