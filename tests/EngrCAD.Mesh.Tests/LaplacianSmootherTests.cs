using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class LaplacianSmootherTests
{
    /// <summary>Open triangulated grid in the z = 0 plane, (size+1)² vertices spanning [0, size]².</summary>
    internal static HalfEdgeMesh PlaneGrid(int size)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= size; j++)
        {
            for (int i = 0; i <= size; i++)
                positions.Add(new Vector3d(i, j, 0));
        }
        var faces = new List<int[]>();
        int Id(int i, int j) => j * (size + 1) + i;
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                faces.Add([Id(i, j), Id(i + 1, j), Id(i + 1, j + 1)]);
                faces.Add([Id(i, j), Id(i + 1, j + 1), Id(i, j + 1)]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static HalfEdgeMesh WithRadialNoise(HalfEdgeMesh sphere, double amplitude, int seed)
    {
        var rng = new Random(seed);
        var (positions, faces) = sphere.ToIndexed();
        for (int i = 0; i < positions.Length; i++)
        {
            var dir = positions[i].Normalized();
            positions[i] += dir * ((rng.NextDouble() * 2 - 1) * amplitude);
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    /// <summary>
    /// Max deviation of a vertex's radius from its one-ring's average radius — a LOCAL
    /// roughness measure. Deviation from the nominal (or even the mean) radius would
    /// conflate noise with the shrinkage curvature flow causes, which is non-uniform on
    /// a UV sphere (pole triangles are denser than equator ones).
    /// </summary>
    private static double RadialRoughness(HalfEdgeMesh mesh)
    {
        double max = 0;
        foreach (var v in mesh.Vertices)
        {
            double ring = v.Neighbors().Average(n => n.Position.Length);
            max = Math.Max(max, Math.Abs(v.Position.Length - ring));
        }
        return max;
    }

    [Fact]
    public void Smooth_PinsBoundaryBitIdentically()
    {
        var grid = PlaneGrid(6);
        var lifted = Lift(grid, 3, 3, 1.5); // bump the middle out of plane
        var smoothed = LaplacianMeshSmoother.Smooth(lifted);

        Assert.Equal(lifted.VertexCount, smoothed.VertexCount);
        foreach (var v in lifted.Vertices)
        {
            if (v.IsBoundary)
                Assert.Equal(v.Position, smoothed.GetPosition(v.Index)); // bitwise
        }
    }

    [Fact]
    public void Smooth_PlanarMeshStaysExactlyPlanar()
    {
        // A planar mesh has b_z = 0 exactly, and the solver's forward/back substitution
        // of a zero vector is zero — so z stays bit-zero, not merely small.
        var grid = PlaneGrid(8);
        var smoothed = LaplacianMeshSmoother.Smooth(grid, new LaplacianSmoothOptions { TimeStep = 5 });
        foreach (var v in smoothed.Vertices)
            Assert.Equal(0.0, v.Position.Z);
    }

    [Fact]
    public void Smooth_ReducesNoiseOnSphere()
    {
        var noisy = WithRadialNoise(MeshPrimitives.UvSphere(5, 32, 16), amplitude: 0.15, seed: 42);
        double before = RadialRoughness(noisy);

        var smoothed = LaplacianMeshSmoother.Smooth(noisy, new LaplacianSmoothOptions { TimeStep = 0.5 });
        double after = RadialRoughness(smoothed);

        Assert.True(after < before * 0.5,
            $"Smoothing should at least halve the radial roughness: {before:F4} -> {after:F4}");
        // Curvature flow shrinks — but a gentle step must not collapse the sphere.
        Assert.True(smoothed.Volume() > 0.9 * noisy.Volume());
        Assert.True(smoothed.Volume() < noisy.Volume());
    }

    [Fact]
    public void Smooth_ClosedMeshHasNoBoundary_AllVerticesSolve()
    {
        var box = MeshPrimitives.Box(2, 2, 2).Triangulated();
        var smoothed = LaplacianMeshSmoother.Smooth(box, new LaplacianSmoothOptions { TimeStep = 0.05 });
        Assert.True(smoothed.IsClosed);
        Assert.True(smoothed.Volume() < box.Volume()); // corners round inward
        Assert.True(smoothed.Volume() > 0.5 * box.Volume());
    }

    [Fact]
    public void Smooth_UniformWeighting_AlsoSmooths()
    {
        // A z-noisy plane with pinned boundary: uniform weights must pull the interior
        // back toward the plane. (A UV sphere is the wrong fixture here — its non-uniform
        // triangle sizes make uniform weights genuinely distort near the poles.)
        var grid = PlaneGrid(8);
        var (positions, faces) = grid.ToIndexed();
        var rng = new Random(7);
        for (int i = 0; i < positions.Length; i++)
        {
            if (!grid.GetVertex(i).IsBoundary)
                positions[i] += new Vector3d(0, 0, (rng.NextDouble() * 2 - 1) * 0.3);
        }
        var noisy = HalfEdgeMesh.Build(positions, faces);
        double before = noisy.Vertices.Max(v => Math.Abs(v.Position.Z));

        var smoothed = LaplacianMeshSmoother.Smooth(
            noisy, new LaplacianSmoothOptions { Weighting = LaplacianWeighting.Uniform, TimeStep = 0.5 });
        double after = smoothed.Vertices.Max(v => Math.Abs(v.Position.Z));
        Assert.True(after < before * 0.5, $"Uniform smoothing should flatten the noise: {before:F4} -> {after:F4}");
    }

    [Fact]
    public void Smooth_FixedVerticesArePinnedBitIdentically()
    {
        var grid = PlaneGrid(6);
        var lifted = Lift(grid, 2, 2, 2.0);
        int pin = Index(6, 4, 4);
        var smoothed = LaplacianMeshSmoother.Smooth(
            lifted, new LaplacianSmoothOptions { FixedVertices = [pin] });
        Assert.Equal(lifted.GetPosition(pin), smoothed.GetPosition(pin)); // bitwise
    }

    [Fact]
    public void Smooth_IsDeterministic()
    {
        var noisy = WithRadialNoise(MeshPrimitives.UvSphere(3, 24, 12), amplitude: 0.1, seed: 3);
        var a = LaplacianMeshSmoother.Smooth(noisy);
        var b = LaplacianMeshSmoother.Smooth(noisy);
        foreach (var v in a.Vertices)
            Assert.Equal(v.Position, b.GetPosition(v.Index)); // bitwise
    }

    [Fact]
    public void Smooth_MultipleIterationsSmoothMore()
    {
        var noisy = WithRadialNoise(MeshPrimitives.UvSphere(5, 24, 12), amplitude: 0.15, seed: 11);
        var once = LaplacianMeshSmoother.Smooth(noisy, new LaplacianSmoothOptions { TimeStep = 0.25 });
        var thrice = LaplacianMeshSmoother.Smooth(noisy, new LaplacianSmoothOptions { TimeStep = 0.25, Iterations = 3 });
        Assert.True(RadialRoughness(thrice) < RadialRoughness(once));
    }

    [Fact]
    public void Smooth_RejectsBadOptions()
    {
        var grid = PlaneGrid(3);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LaplacianMeshSmoother.Smooth(grid, new LaplacianSmoothOptions { TimeStep = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LaplacianMeshSmoother.Smooth(grid, new LaplacianSmoothOptions { Iterations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LaplacianMeshSmoother.Smooth(grid, new LaplacianSmoothOptions { FixedVertices = [999] }));
    }

    internal static int Index(int size, int i, int j) => j * (size + 1) + i;

    private static HalfEdgeMesh Lift(HalfEdgeMesh grid, int i, int j, double height)
    {
        var (positions, faces) = grid.ToIndexed();
        int size = (int)Math.Sqrt(positions.Length) - 1;
        positions[Index(size, i, j)] += new Vector3d(0, 0, height);
        return HalfEdgeMesh.Build(positions, faces);
    }
}
