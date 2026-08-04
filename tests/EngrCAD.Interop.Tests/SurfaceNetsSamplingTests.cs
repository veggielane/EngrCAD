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
/// <para>
/// The goldens are in TWO tables on purpose. Topology (counts and face indices) is
/// asserted for both vertex-placement rules from ONE row, because sharp-feature placement
/// must not move a single index — that is the manifoldness argument stated as a test
/// rather than as prose. Positions are a separate table with a row per rule, and the
/// <c>plain</c> rows are the original goldens taken from the dense <c>Vector3d[]</c>
/// sampler that preceded the deinterleaved one, so the pre-dual-contouring output stays
/// pinned against every revision since.
/// </para>
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

    /// <summary>
    /// The FNV over face indices alone — the mesh's combinatorics with no coordinate in
    /// it. Sharp-feature placement moves every vertex and must not move this by a bit:
    /// which crossings belong to which vertex is decided before any position is computed,
    /// so the index buffer handed to <see cref="HalfEdgeMesh.Build"/> is the same buffer.
    /// That is the whole manifoldness argument, expressed as one number.
    /// </summary>
    private static long TopologyFingerprint(HalfEdgeMesh mesh)
    {
        unchecked
        {
            long hash = (long)14695981039346656037UL;
            void Mix(long value)
            {
                hash ^= value;
                hash *= 1099511628211L;
            }

            Mix(mesh.VertexCount);
            foreach (var face in mesh.Faces)
                foreach (var vertex in face.Vertices())
                    Mix(vertex.Index);
            return hash;
        }
    }

    /// <summary>
    /// Counts and TOPOLOGY, asserted for BOTH vertex-placement rules — one row, two
    /// polygonizations. Positions live in <see cref="Positions"/>, which is deliberately a
    /// separate table: they are the thing sharp features are allowed to change.
    /// </summary>
    public static TheoryData<string, int, int, int, long> Golden => new()
    {
        // name, resolution, expected vertices, expected faces, topology fingerprint
        { "sphere", 32, 2528, 2526, -1880870020840074829L },
        { "csg", 41, 3316, 3316, 324766280307659388L },
        { "torus", 37, 2764, 2764, 7645207557967803343L },
    };

    /// <summary>
    /// Position fingerprints, one per placement rule. The <c>plain</c> column is the
    /// ORIGINAL golden, taken from the dense <c>Vector3d[]</c> sampler that preceded the
    /// deinterleaved one — kept verbatim rather than retired, so the pre-sharp-feature
    /// output stays pinned against every revision since; the <c>sharp</c> column was taken
    /// deliberately when dual contouring landed.
    /// </summary>
    public static TheoryData<string, int, bool, long> Positions => new()
    {
        { "sphere", 32, false, -1701506304702635191L },
        { "csg", 41, false, -6493424247366869703L },
        { "torus", 37, false, -3233375004565935246L },
        { "sphere", 32, true, 8816252880545085349L },
        { "csg", 41, true, -131546108220177304L },
        { "torus", 37, true, -7311666246686429445L },
    };

    private static readonly SurfaceNetsOptions Plain = new() { SharpFeatures = false };

    private static (Sdf Field, Aabb Region) Case(string name) => name switch
    {
        "sphere" => (Sdf.Sphere(1.0), new Aabb((-1.4, -1.4, -1.4), (1.4, 1.4, 1.4))),
        "csg" => (Csg(), new Aabb((-2.2, -2.2, -2.2), (2.4, 2.2, 2.2))),
        "torus" => (Sdf.Torus(1.0, 0.35).Rotate(Quaterniond.FromAxisAngle(Vector3d.UnitX, 0.7)),
            new Aabb((-1.6, -1.6, -1.6), (1.6, 1.6, 1.6))),
        // Three components with nothing but empty space between them, one of them small
        // enough to sit inside a single cull block: the case a seed-and-flood continuation
        // would drop and a Lipschitz cull cannot.
        "scattered" => (
            Sdf.Union(
                Sdf.Sphere(0.9),
                Sdf.Box(0.5, 0.5, 0.5).Translate((3.2, -2.8, 2.6)),
                Sdf.Sphere(0.22).Translate((-3.0, 3.1, -2.7))),
            new Aabb((-4.0, -4.0, -4.0), (4.0, 4.0, 4.0))),
        // A hollow shell: two nested surfaces a few cells apart, so the cull's kept region
        // is a thick spherical band and both walls must survive it. (Thinner than a cell
        // and Surface Nets itself gives up — the sampled field stops registering inside —
        // so this is deliberately three cells thick at the resolutions tested.)
        "thin shell" => (Sdf.Sphere(1.2).Shell(0.2),
            new Aabb((-1.6, -1.6, -1.6), (1.6, 1.6, 1.6))),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Polygonize_MatchesTheGoldenTopology(
        string name, int resolution, int vertices, int faces, long topology)
    {
        var (field, region) = Case(name);
        foreach (var options in new[] { Plain, SurfaceNetsOptions.Default })
        {
            var mesh = SurfaceNets.Polygonize(field, region, resolution, null, options);
            Assert.Equal(vertices, mesh.VertexCount);
            Assert.Equal(faces, mesh.FaceCount);
            Assert.Equal(topology, TopologyFingerprint(mesh));
        }
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void Polygonize_MatchesTheGoldenPositions(
        string name, int resolution, bool sharpFeatures, long fingerprint)
    {
        var (field, region) = Case(name);
        var options = sharpFeatures ? SurfaceNetsOptions.Default : Plain;
        Assert.Equal(fingerprint, Fingerprint(SurfaceNets.Polygonize(field, region, resolution, null, options)));
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

    /// <summary>
    /// The surface cull is a work filter, never a numerical one. It removes blocks the field
    /// provably cannot reach — so the walk that skips them must produce EXACTLY the mesh the
    /// walk that visits everything produces: same vertices, same faces, same ordering.
    /// <para>"scattered" and "thin shell" are the cases that would catch an incomplete visit
    /// set: three components separated by empty space (one of them smaller than a cull
    /// block), and a shell thinner than a cell. A seed-and-flood continuation drops the
    /// former unless its seeds are already complete; the Lipschitz cull is complete by
    /// construction, which is why the cull IS the algorithm here and no flood is needed.</para>
    /// </summary>
    [Theory]
    [InlineData("sphere", 40)]
    [InlineData("csg", 37)]
    [InlineData("torus", 43)]
    [InlineData("scattered", 64)]
    [InlineData("thin shell", 48)]
    public void TheCulledWalk_IsBitIdenticalToTheFullWalk(string name, int resolution)
    {
        var (field, region) = Case(name);
        var full = SurfaceNets.Polygonize(field, region, resolution, null, int.MaxValue, cull: false);
        var culled = SurfaceNets.Polygonize(field, region, resolution, null, int.MaxValue);

        Assert.True(full.VertexCount > 0);
        Assert.Equal(full.VertexCount, culled.VertexCount);
        Assert.Equal(full.FaceCount, culled.FaceCount);
        Assert.Equal(Fingerprint(full), Fingerprint(culled));
    }

    /// <summary>
    /// …and the cull composes with the slab window, which slides on a completely different
    /// schedule: a two-slab budget over a culled walk must still be the dense full walk.
    /// </summary>
    [Theory]
    [InlineData("scattered", 53)]
    [InlineData("thin shell", 39)]
    public void CullAndStreaming_Compose(string name, int resolution)
    {
        var (field, region) = Case(name);
        long full = Fingerprint(
            SurfaceNets.Polygonize(field, region, resolution, null, int.MaxValue, cull: false));
        Assert.Equal(full, Fingerprint(SurfaceNets.Polygonize(field, region, resolution, null, 1)));
    }

    /// <summary>
    /// The separated components come back as separated components — a direct check that the
    /// cull loses nothing, phrased as geometry rather than as a hash.
    /// </summary>
    [Fact]
    public void ScatteredComponents_AllSurvive()
    {
        var (field, region) = Case("scattered");
        var mesh = SurfaceNets.Polygonize(field, region, 64);
        var components = MeshConnectedComponents.Find(mesh);
        Assert.Equal(3, components.Count);
        Assert.All(components, c => Assert.True(c.IsClosed));
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
