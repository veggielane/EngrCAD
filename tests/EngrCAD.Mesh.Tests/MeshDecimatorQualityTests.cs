using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The committed record of porting <see cref="MeshDecimator"/> onto
/// <see cref="EditableMesh.CollapseEdge"/>, and the permanent regression guard that came out
/// of it. The port was measured against the previous indexed-face-set implementation on the
/// twelve fixture/budget pairs below and produced <b>bit-identical geometry</b> on every one,
/// at <b>0.84x</b> the time (Release, best of 9 after a 1.5 s warm-up budget: 1.87 ms -> 1.57 ms
/// decimating a 2000-triangle sphere to 400) — so the old implementation was deleted rather
/// than kept as dead weight, and its measured error table lives on here as the baseline.
/// <para>
/// One real bug surfaced while building the port, and it is worth remembering: folding the
/// priority-queue seeding into the quadric-accumulation loop keys the whole initial queue off
/// <i>partially accumulated</i> quadrics. Every mesh still came out closed, manifold and at the
/// requested face count — the only symptom was a 2.4x worse approximation error at light
/// decimation, where the cheapest (earliest) choices dominate the result. Without this
/// baseline table it would have shipped unnoticed.
/// </para>
/// </summary>
public class MeshDecimatorQualityTests(ITestOutputHelper output)
{
    /// <summary>Fixture, face budget, and the measured mean/max one-sided error as a fraction of the model extent.</summary>
    private static readonly (string Fixture, int Target, int Faces, double Mean, double Max)[] Baseline =
    [
        ("jittered", 1000, 1000, 0.000505, 0.002041),
        ("jittered",  400,  400, 0.001472, 0.004661),
        ("jittered",  200,  200, 0.002635, 0.008868),
        ("jittered",  120,  120, 0.004353, 0.012885),
        ("jittered",   40,   40, 0.013197, 0.033492),
        ("cylinder",  400,  188, 0.000000, 0.000000),
        ("cylinder",  200,  188, 0.000000, 0.000000),
        ("cylinder",   60,   60, 0.002102, 0.005563),
        ("grid",      600,  600, 0.000505, 0.001981),
        ("grid",      300,  300, 0.001153, 0.004930),
        ("grid",      150,  150, 0.002669, 0.016132),
        ("grid",       60,   96, 0.051450, 0.190085),
    ];

    /// <summary>One-sided sampled distance from <paramref name="from"/>'s vertices to <paramref name="to"/>'s surface.</summary>
    private static (double Mean, double Max) SurfaceError(HalfEdgeMesh from, HalfEdgeMesh to)
    {
        var target = new MeshProjectionTarget(to);
        double sum = 0, max = 0;
        foreach (var vertex in from.Vertices)
        {
            double d = (target.Project(vertex.Position) - vertex.Position).Length;
            sum += d;
            max = Math.Max(max, d);
        }
        return (sum / from.VertexCount, max);
    }

    private static HalfEdgeMesh Fixture(string name) => name switch
    {
        "jittered" => Jittered(MeshPrimitives.UvSphere(1.0, 40, 26).Triangulated()),
        "cylinder" => MeshPrimitives.Cylinder(1.0, 3.0, 48).Triangulated(),
        _ => Grid(24),
    };

    /// <summary>
    /// A UV sphere is perfectly symmetric, so whole rings of edges carry <b>exactly</b> equal
    /// quadric error and the decimation becomes one long chain of ties — which makes any
    /// comparison measure tie-breaking rather than quality. A deterministic, seeded 1% radial
    /// wobble breaks the symmetry.
    /// </summary>
    private static HalfEdgeMesh Jittered(HalfEdgeMesh mesh)
    {
        var random = new Random(12345); // seeded: the baseline must be reproducible
        var (positions, faces) = mesh.ToIndexed();
        for (int i = 0; i < positions.Length; i++)
            positions[i] *= 1.0 + 0.01 * (random.NextDouble() - 0.5);
        return HalfEdgeMesh.Build(positions, faces);
    }

    private static HalfEdgeMesh Grid(int n)
    {
        var positions = new List<Vector3d>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                double x = (double)i / n, y = (double)j / n;
                positions.Add(new Vector3d(x, y, 0.3 * Math.Sin(6 * x) * Math.Cos(5 * y)));
            }
        }
        var faces = new List<int[]>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                faces.Add([a, b, d]);
                faces.Add([a, d, c]);
            }
        }
        return HalfEdgeMesh.Build(positions, faces);
    }

    [Fact]
    public void ApproximationErrorMatchesTheRecordedBaseline()
    {
        foreach (var (fixture, target, faces, mean, max) in Baseline)
        {
            var mesh = Fixture(fixture);
            var decimated = MeshDecimator.Decimate(mesh, target);
            var error = SurfaceError(mesh, decimated);
            double extent = mesh.ComputeBounds().Size.Length;
            output.WriteLine($"{fixture,-9} -> {target,4}: faces {decimated.FaceCount,5}, " +
                             $"mean {error.Mean / extent:P4} (baseline {mean:P4}), " +
                             $"max {error.Max / extent:P4} (baseline {max:P4})");

            Assert.Equal(faces, decimated.FaceCount);
            AssertNearBaseline(error.Mean / extent, mean, $"{fixture} -> {target} mean");
            AssertNearBaseline(error.Max / extent, max, $"{fixture} -> {target} max");
        }

        // The baseline is quoted to six significant figures, so half a percent of relative
        // slack is rounding, not tolerance for drift.
        static void AssertNearBaseline(double actual, double expected, string what) =>
            Assert.True(Math.Abs(actual - expected) <= 0.005 * expected + 1e-12,
                $"{what}: {actual:E6} vs baseline {expected:E6}");
    }

    [Fact]
    public void PreservesClosednessAndBoundaries()
    {
        var sphere = MeshPrimitives.UvSphere(1.0, 30, 20).Triangulated();
        var closed = MeshDecimator.Decimate(sphere, 200);
        Assert.True(closed.IsClosed);
        Assert.Equal(2, closed.EulerCharacteristic);

        var patch = Grid(16);
        var open = MeshDecimator.Decimate(patch, 100);
        Assert.Single(open.BoundaryLoops());
        // Boundary vertices are never collapsed, so the outline survives vertex for vertex.
        foreach (var p in patch.BoundaryLoops()[0].Select(he => he.Origin.Position))
            Assert.Contains(open.Vertices, v => (v.Position - p).Length == 0);
    }

    /// <summary>
    /// The port's substantive improvement over the deleted implementation: its degeneracy
    /// guards are relative to the model extent and its quadric normals use an exact-zero
    /// guard, where the old one applied the absolute 1e-9 weld tolerance to a cross product
    /// (an AREA) and an absolute 1e-24 to a squared one. Measured before the switch: at 1e-5
    /// scale the old path built no quadrics at all and lost <b>91%</b> of the volume.
    /// </summary>
    [Fact]
    public void WorksAtMicronScale()
    {
        const double scale = 1e-5;
        var sphere = MeshPrimitives.UvSphere(scale, 30, 20).Triangulated();
        double exact = 4.0 / 3.0 * Math.PI * scale * scale * scale;

        var decimated = MeshDecimator.Decimate(sphere, 200);
        double error = Math.Abs(decimated.Volume() - exact) / exact;
        output.WriteLine($"1e-5 scale: volume error {error:P3} (deleted implementation: 91.149%)");

        Assert.True(decimated.IsClosed);
        Assert.True(error < 0.05, $"volume error {error:P3}");
    }
}
