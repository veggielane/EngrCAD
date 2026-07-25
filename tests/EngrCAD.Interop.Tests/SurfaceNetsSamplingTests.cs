using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Locks the sampler's two invariants: the mesh is bit-for-bit independent of how the
/// grid is fed to the field (deinterleaved streaming vs. a point at a time), and
/// bit-for-bit independent of the slab window size the streaming sampler chooses.
/// The golden fingerprints were taken from the dense <c>Vector3d[]</c> sampler that
/// preceded the deinterleaved one, so they also pin the output against that revision.
/// </summary>
public class SurfaceNetsSamplingTests
{
    /// <summary>FNV-1a over every vertex coordinate's exact bits, then the face indices.</summary>
    private static long Fingerprint(HalfEdgeMesh mesh)
    {
        unchecked
        {
            long hash = (long)14695981039346656037UL;
            void Mix(long value)
            {
                hash ^= value;
                hash *= 1099511628211L;
            }

            foreach (var vertex in mesh.Vertices)
            {
                var p = mesh.GetPosition(vertex.Index);
                Mix(BitConverter.DoubleToInt64Bits(p.X));
                Mix(BitConverter.DoubleToInt64Bits(p.Y));
                Mix(BitConverter.DoubleToInt64Bits(p.Z));
            }
            foreach (var face in mesh.Faces)
                foreach (var vertex in face.Vertices())
                    Mix(vertex.Index);
            return hash;
        }
    }

    private static Sdf Csg() =>
        (Sdf.Box(2, 2, 2) - Sdf.Cylinder(0.6, 3))
        .SmoothUnion(Sdf.Sphere(1.2).Translate((0.8, 0.3, 0.2)), 0.25);

    public static TheoryData<string, int, int, int, long> Golden => new()
    {
        // name, resolution, expected vertices, expected faces, expected fingerprint
        { "sphere", 32, 2528, 2526, -1701506304702635191L },
        { "csg", 41, 3316, 3316, -6493424247366869703L },
        { "torus", 37, 2764, 2764, -3233375004565935246L },
    };

    private static (Sdf Field, Aabb Region) Case(string name) => name switch
    {
        "sphere" => (Sdf.Sphere(1.0), new Aabb((-1.4, -1.4, -1.4), (1.4, 1.4, 1.4))),
        "csg" => (Csg(), new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2))),
        "torus" => (Sdf.Torus(1.0, 0.35).Rotate(Quaterniond.FromAxisAngle(Vector3d.UnitX, 0.7)),
            new Aabb((-1.6, -1.6, -1.6), (1.6, 1.6, 1.6))),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Polygonize_MatchesTheGoldenBitPattern(
        string name, int resolution, int vertices, int faces, long fingerprint)
    {
        var (field, region) = Case(name);
        var mesh = SurfaceNets.Polygonize(field, region, resolution);

        Assert.Equal(vertices, mesh.VertexCount);
        Assert.Equal(faces, mesh.FaceCount);
        Assert.Equal(fingerprint, Fingerprint(mesh));
    }

    /// <summary>
    /// Feeding the field deinterleaved coordinates must give exactly what evaluating it
    /// one point at a time gives. <see cref="ScalarOnly"/> defeats every vector kernel in
    /// the AST by forcing the batch seam back through the scalar entry point.
    /// </summary>
    [Theory]
    [InlineData("sphere", 24)]
    [InlineData("csg", 29)]
    [InlineData("torus", 31)]
    public void BatchSampling_IsBitIdenticalToScalarEvaluation(string name, int resolution)
    {
        var (field, region) = Case(name);
        var batched = SurfaceNets.Polygonize(field, region, resolution);
        var scalar = SurfaceNets.Polygonize(new ScalarOnly(field), region, resolution);

        Assert.Equal(batched.VertexCount, scalar.VertexCount);
        Assert.Equal(Fingerprint(batched), Fingerprint(scalar));
    }

    /// <summary>
    /// The slab window is a memory knob, never a numerical one: a budget of two slabs
    /// (the minimum — the sampler slides one slab at a time) must reproduce the dense
    /// window's mesh exactly, including vertex and face ordering.
    /// </summary>
    [Theory]
    [InlineData("sphere", 24)]
    [InlineData("csg", 29)]
    [InlineData("torus", 31)]
    public void StreamedSlabs_AreBitIdenticalToADenseWindow(string name, int resolution)
    {
        var (field, region) = Case(name);
        var dense = SurfaceNets.Polygonize(field, region, resolution, null, int.MaxValue);
        var streamed = SurfaceNets.Polygonize(field, region, resolution, null, 1);

        Assert.Equal(dense.VertexCount, streamed.VertexCount);
        Assert.Equal(dense.FaceCount, streamed.FaceCount);
        Assert.Equal(Fingerprint(dense), Fingerprint(streamed));
    }

    /// <summary>An awkward window size (neither the whole grid nor the minimum) must not
    /// change anything either — window boundaries fall between different slabs.</summary>
    [Fact]
    public void AnOddWindowSize_ChangesNothing()
    {
        var (field, region) = Case("csg");
        long dense = Fingerprint(SurfaceNets.Polygonize(field, region, 29, null, int.MaxValue));
        // 29 cells → 30 slabs of 30×30 samples; these budgets give 5-, 3- and 2-slab windows.
        foreach (int budget in new[] { 4500, 2700, 1800 })
            Assert.Equal(dense, Fingerprint(SurfaceNets.Polygonize(field, region, 29, null, budget)));
    }

    /// <summary>Streaming must survive a grid whose window slides many times.</summary>
    [Fact]
    public void ManyWindowSlides_StillProduceAClosedSphere()
    {
        var mesh = SurfaceNets.Polygonize(
            Sdf.Sphere(1), new Aabb((-1.3, -1.3, -1.3), (1.3, 1.3, 1.3)), 48, null, 1);
        Assert.True(mesh.IsClosed);
        // 4/3·π·r³ = 4.18879; Surface Nets under-reports slightly at this resolution.
        Assert.Equal(4.18879, mesh.Volume(), 1);
    }

    /// <summary>Forces every batch through the scalar <c>Evaluate</c>, one point at a time.</summary>
    private sealed class ScalarOnly(Sdf inner) : Sdf
    {
        public override double Evaluate(in Vector3d point) => inner.Evaluate(point);

        public override Aabb Bounds => inner.Bounds;

        protected override void EvaluateBatch(
            ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
        {
            for (int i = 0; i < x.Length; i++)
                distances[i] = inner.Evaluate(new Vector3d(x[i], y[i], z[i]));
        }
    }
}
