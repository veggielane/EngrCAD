using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class MeshSdfTests
{
    [Fact]
    public void BoxMesh_MatchesAnalyticBoxSdf()
    {
        // A box mesh is geometrically identical to the analytic box SDF, so signed
        // distances must agree everywhere: face, edge, and corner regions, inside and out.
        var meshSdf = new MeshSdf(MeshPrimitives.Box(2, 2, 2));
        var analytic = Sdf.Box(2, 2, 2);

        var rng = new Random(31);
        for (int i = 0; i < 300; i++)
        {
            var p = new Vector3d(
                rng.NextDouble() * 6 - 3,
                rng.NextDouble() * 6 - 3,
                rng.NextDouble() * 6 - 3);
            Assert.Equal(analytic.Evaluate(p), meshSdf.Evaluate(p), 9);
        }
    }

    [Fact]
    public void SphereMesh_ApproximatesAnalyticSphere()
    {
        var meshSdf = new MeshSdf(MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24));
        double chordError = 1.0 - Math.Cos(Math.PI / 24); // max tessellation deviation

        var rng = new Random(37);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                rng.NextDouble() * 4 - 2,
                rng.NextDouble() * 4 - 2,
                rng.NextDouble() * 4 - 2);
            double expected = p.Length - 1.0;
            Assert.True(Math.Abs(meshSdf.Evaluate(p) - expected) < chordError + 1e-9,
                $"at {p}: mesh sdf {meshSdf.Evaluate(p)} vs analytic {expected}");
        }
    }

    [Fact]
    public void Evaluate_SteadyState_DoesNotAllocate()
    {
        // Perf mandate: Evaluate is the hottest kernel path (every SDF sample of every
        // bridged shape funnels through it). The BVH nearest-triangle search uses a
        // struct metric (Bvh.Nearest<TMetric>), so steady-state evaluation must be
        // allocation-free — the old lambda overload cost a closure per call.
        var sdf = new MeshSdf(MeshPrimitives.UvSphere(1.0, segments: 24, rings: 12));

        var rng = new Random(43);
        var points = new Vector3d[256];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Vector3d(
                rng.NextDouble() * 4 - 2,
                rng.NextDouble() * 4 - 2,
                rng.NextDouble() * 4 - 2);
        }

        double sink = 0;
        void RunBatch(int iterations)
        {
            for (int i = 0; i < iterations; i++)
                sink += sdf.Evaluate(points[i & 255]);
        }

        RunBatch(5000); // warmup — let tiered compilation settle

        const int iterations = 20_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunBatch(iterations);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        // A per-call closure would cost ≳ 88 B × 20 000 ≈ 1.7 MB; steady state must be
        // (near-)zero. Allow a small one-time slack so background tiering can't flake.
        Assert.True(delta < 1024,
            $"MeshSdf.Evaluate allocated {delta} bytes over {iterations} calls ({(double)delta / iterations:F2} B/call)");
        Assert.True(double.IsFinite(sink)); // keep the loop observable
    }

    [Fact]
    public void SignIsNegativeInsideAndPositiveOutside()
    {
        var sdf = new MeshSdf(MeshPrimitives.Cylinder(1, 2, segments: 32));
        Assert.True(sdf.Evaluate((0, 0, 1)) < 0);       // axis, mid-height
        Assert.True(sdf.Evaluate((0.9, 0, 0.1)) < 0);   // near bottom rim, inside
        Assert.True(sdf.Evaluate((2, 0, 1)) > 0);       // radially outside
        Assert.True(sdf.Evaluate((1.2, 1.2, 2.5)) > 0); // above the top rim, outside
    }

    [Fact]
    public void RoundTrip_MeshToSdfToMesh()
    {
        var original = MeshPrimitives.UvSphere(1.0, segments: 32, rings: 16);
        var remeshed = SurfaceNets.Polygonize(new MeshSdf(original), resolution: 40);
        remeshed.Validate();

        Assert.True(remeshed.IsClosed);
        Assert.Equal(2, remeshed.EulerCharacteristic);
        Assert.True(Math.Abs(remeshed.Volume() - original.Volume()) / original.Volume() < 0.05,
            $"round-trip volume {remeshed.Volume()} vs original {original.Volume()}");
    }

    [Fact]
    public void ComposesWithAnalyticSdfs()
    {
        // The point of the hybrid kernel: mesh geometry as a first-class implicit node.
        var meshBox = new MeshSdf(MeshPrimitives.Box(1.6, 1.6, 1.6));
        var hybrid = meshBox.SmoothUnion(Sdf.Sphere(0.7).Translate((1.1, 0, 0)), 0.3);

        Assert.True(hybrid.Evaluate((0, 0, 0)) < 0);      // inside the mesh part
        Assert.True(hybrid.Evaluate((1.4, 0, 0)) < 0);    // inside the sphere part
        Assert.True(hybrid.Evaluate((3, 3, 3)) > 0);

        var mesh = SurfaceNets.Polygonize(hybrid, resolution: 48);
        mesh.Validate();
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.Volume() > 1.6 * 1.6 * 1.6, "blend must add volume beyond the box");
    }

    [Fact]
    public void OpenMesh_IsRejected()
    {
        var open = HalfEdgeMesh.Build(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)],
            [new[] { 0, 1, 2 }]);
        Assert.Throws<ArgumentException>(() => new MeshSdf(open));
    }

    [Fact]
    public void WindingSignSource_MatchesPseudonormalSign_OnClosedMesh()
    {
        // Opt-in winding sign source must produce the same inside/outside partition (same
        // distance magnitude, same sign) as the default pseudonormal source on a watertight
        // mesh — it only changes *how* the sign is decided.
        var mesh = MeshPrimitives.UvSphere(1.0, segments: 40, rings: 20);
        var pseudonormal = new MeshSdf(mesh);
        var winding = new MeshSdf(mesh, MeshSignSource.WindingNumber);

        var rng = new Random(19);
        for (int i = 0; i < 400; i++)
        {
            var p = new Vector3d(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            double a = pseudonormal.Evaluate(p);
            double b = winding.Evaluate(p);
            Assert.Equal(Math.Abs(a), Math.Abs(b), 9);      // same distance
            if (Math.Abs(a) > 1e-6)
                Assert.Equal(Math.Sign(a), Math.Sign(b));   // same sign off the surface
        }
    }

    [Fact]
    public void WindingSignSource_AcceptsOpenMesh_AndSignsInteriorNegative()
    {
        // A sphere with a cap removed is not watertight, so the default source rejects it;
        // the winding source accepts it and still signs the interior negative.
        var open = SphereWithHole();
        Assert.False(open.IsClosed);
        Assert.Throws<ArgumentException>(() => new MeshSdf(open));

        var sdf = new MeshSdf(open, MeshSignSource.WindingNumber);
        Assert.True(sdf.Evaluate((0, 0, -0.5)) < 0);  // interior, away from the hole
        Assert.True(sdf.Evaluate((2.0, 0, 0)) > 0);   // clearly outside
        Assert.True(sdf.Evaluate((0, 0, -1.6)) > 0);  // below, outside
    }

    private static HalfEdgeMesh SphereWithHole()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, segments: 48, rings: 24);
        var dir = Vector3d.UnitZ;
        var (positions, faces) = sphere.ToIndexed();
        var kept = new List<int[]>();
        foreach (var face in faces)
        {
            var centroid = Vector3d.Zero;
            foreach (int v in face)
                centroid += positions[v];
            if ((centroid / face.Length).Normalized().Dot(dir) < 0.9)
                kept.Add(face);
        }
        var remap = new Dictionary<int, int>();
        var newPositions = new List<Vector3d>();
        var newFaces = new List<int[]>(kept.Count);
        foreach (var face in kept)
        {
            var nf = new int[face.Length];
            for (int i = 0; i < face.Length; i++)
            {
                if (!remap.TryGetValue(face[i], out int idx))
                {
                    idx = newPositions.Count;
                    remap[face[i]] = idx;
                    newPositions.Add(positions[face[i]]);
                }
                nf[i] = idx;
            }
            newFaces.Add(nf);
        }
        return HalfEdgeMesh.Build(newPositions, newFaces);
    }
}
