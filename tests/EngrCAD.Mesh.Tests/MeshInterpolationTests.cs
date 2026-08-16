using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// <see cref="MeshProjectionTarget.TryInterpolate"/> — the nearest triangle plus
/// barycentric weights, what a per-vertex field (a displacement) is evaluated with at an
/// arbitrary on-surface point (the feature-edge overlay's samples are exact B-Rep curve
/// points, not mesh vertices).
/// </summary>
public class MeshInterpolationTests
{
    [Fact]
    public void WeightsAreAConvexCombination_AndReproduceAnAffineField()
    {
        // Barycentric interpolation of an affine function is exact wherever the query
        // point lies in a facet's plane — the property the displaced edge overlay rests
        // on. The box's facets are planar, so every on-surface probe qualifies.
        var mesh = MeshPrimitives.Box(8, 6, 4);
        var target = new MeshProjectionTarget(mesh);
        static double F(in Vector3d p) => 3 + 0.5 * p.X - 0.25 * p.Y + 2 * p.Z;

        // MeshPrimitives.Box is centred on the origin: [-4,4] x [-3,3] x [-2,2].
        Vector3d[] probes =
        [
            new(0, 0, 2),        // top-face interior
            new(-4, 0, 0),       // side-face interior
            new(4, 3, 2),        // a corner (a mesh vertex)
            new(0, -3, -2),      // a bottom edge midpoint
            new(1.25, 3, 0.5),   // an off-centre face point
        ];
        foreach (var p in probes)
        {
            Assert.True(target.TryInterpolate(p, out var corners, out var weights));
            Assert.InRange(weights.A, 0, 1);
            Assert.InRange(weights.B, 0, 1);
            Assert.InRange(weights.C, 0, 1);
            Assert.InRange(Math.Abs(weights.A + weights.B + weights.C - 1), 0, 1e-12);

            double interpolated =
                F(mesh.GetPosition(corners.A)) * weights.A
                + F(mesh.GetPosition(corners.B)) * weights.B
                + F(mesh.GetPosition(corners.C)) * weights.C;
            Assert.InRange(Math.Abs(interpolated - F(p)), 0, 1e-12);
        }
    }

    [Fact]
    public void AVertexProbe_PutsAllItsWeightOnThatVertex()
    {
        var mesh = MeshPrimitives.Box(2, 2, 2);
        var target = new MeshProjectionTarget(mesh);
        var corner = new Vector3d(1, 1, 1);
        Assert.True(target.TryInterpolate(corner, out var corners, out var weights));

        // One corner of the winning triangle IS the probe, and it takes weight 1.
        var positions = new[]
        {
            mesh.GetPosition(corners.A), mesh.GetPosition(corners.B), mesh.GetPosition(corners.C),
        };
        var w = new[] { weights.A, weights.B, weights.C };
        int at = Array.IndexOf(positions, corner);
        Assert.True(at >= 0);
        Assert.InRange(Math.Abs(w[at] - 1), 0, 1e-12);
    }
}
