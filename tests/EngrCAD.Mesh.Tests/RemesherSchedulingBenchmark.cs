using System.Diagnostics;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// What <see cref="RemeshScheduling.Queue"/> and <see cref="RemeshOptions.FastSplitPasses"/>
/// are worth, as a committed benchmark rather than a remembered number. Inert unless
/// <c>ENGRCAD_BENCH</c> is set, so a normal <c>dotnet test</c> run pays nothing:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Mesh.Tests -c Release --filter FullyQualifiedName~RemesherSchedulingBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// <b>Release only.</b> In DEBUG, <see cref="EditableMesh"/> runs a full <c>Validate()</c>
/// after every operator, which makes every remesh O(n) per operation and every timing here
/// meaningless. Numbers are best-of-5 after a 1.5 s wall-clock warm-up budget, per this
/// project's rule that a warm-up COUNT is not a warm-up (JIT tiering has moved the same
/// binary's throughput by 3.7× across runs).
/// </para>
/// <para>
/// Reference machine (win-x64, .NET 10.0.302, Release, otherwise idle). Input is
/// <c>UvSphere(1, 96, 64)</c> triangulated — 12 096 faces — remeshed to a target edge of
/// 0.05 against a <see cref="MeshProjectionTarget"/> of itself:
/// </para>
/// <code>
/// passes | scheduling           |    ms | faces | out of band
///     12 | sweep                |   243 | 14570 |        2.5%
///     12 | sweep + fast split   |   239 | 15896 |        1.2%
///     12 | queue                |   240 | 14430 |        4.0%
///     12 | queue + fast split   |   190 | 16010 |        1.5%
///     40 | sweep                |   777 | 13918 |        0.2%
///     40 | sweep + fast split   |   775 | 14806 |        0.3%
///     40 | queue                |   532 | 13808 |        0.4%
///     40 | queue + fast split   |   331 | 15576 |        0.6%
/// </code>
/// <para>
/// <b>Neither feature pays on its own under sweep scheduling, and that is the finding.</b>
/// While the whole mesh is still active, queue scheduling is a wash (240 against 243): its
/// per-vertex ring walk to re-queue a neighbourhood costs about what skipping the quiet parts
/// saves, because there are no quiet parts yet. It pays once regions converge — 1.46× at 40
/// passes. The fast split prepass alone is likewise a wash under sweep (239 against 243),
/// since a sweep visits everything every pass regardless of how the density got there. The
/// two are <b>complementary</b>: reaching the target density sooner is exactly what lets
/// regions go quiet sooner, so together they run 2.35× the plain sweep at 40 passes and
/// 1.28× at 12. The cost is a slightly less relaxed mesh (0.6% of edges outside the length
/// band against the sweep's 0.2%), which is the documented difference, not a regression.
/// </para>
/// </summary>
public class RemesherSchedulingBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static HalfEdgeMesh Subject() =>
        MeshPrimitives.UvSphere(1.0, segments: 96, rings: 64).Triangulated();

    private static RemeshOptions Base(double target) => new(target)
    {
        Iterations = 12,
        FeatureAngleDegrees = 0, // a tessellated sphere's facets are not creases
    };

    private static double BestOf(Func<RemeshResult> run, int trials, out RemeshResult last)
    {
        double best = double.PositiveInfinity;
        RemeshResult? result = null;
        for (int i = 0; i < trials; i++)
        {
            var watch = Stopwatch.StartNew();
            result = run();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        last = result!;
        return best;
    }

    /// <summary>Fraction of edges outside the [0.66 L, 1.33 L] band — the convergence metric.</summary>
    private static double OutOfBand(HalfEdgeMesh mesh, double target)
    {
        int total = 0, outside = 0;
        foreach (var edge in mesh.Edges)
        {
            double length = (edge.Destination.Position - edge.Origin.Position).Length;
            total++;
            if (length < 0.66 * target || length > 1.33 * target)
                outside++;
        }
        return total == 0 ? 0 : (double)outside / total;
    }

    /// <summary>
    /// What restricting <see cref="RemeshProjection.FaceAligned"/>'s accumulation to the faces
    /// incident to the active set is worth. Face-aligned projection used to walk every face
    /// every pass whatever the scheduler had decided, so it was the one stage that did not
    /// compose with <see cref="RemeshScheduling.Queue"/>.
    /// <para>
    /// Reference machine (win-x64, .NET 10.0.302, Release, otherwise idle), input
    /// <c>UvSphere(1, 48, 32)</c> triangulated — 2 976 faces — face-aligned onto a
    /// <see cref="MeshProjectionTarget"/> of itself at a 0.08 target:
    /// </para>
    /// <code>
    /// passes | scheduling | accumulation |    ms
    ///     12 | sweep      | whole mesh   |   113
    ///     12 | queue      | whole mesh   |   124
    ///     12 | queue      | incident     |   123
    ///     40 | sweep      | whole mesh   |   364
    ///     40 | queue      | whole mesh   |   322
    ///     40 | queue      | incident     |   285
    ///    100 | sweep      | whole mesh   |   873
    ///    100 | queue      | whole mesh   |   623
    ///    100 | queue      | incident     |   302
    /// </code>
    /// <para>
    /// <b>The shape matters more than any single ratio.</b> Restricting the accumulation is a
    /// wash at 12 passes (123 against 124 — nothing has gone quiet yet, the same result queue
    /// scheduling itself gives), 1.13× at 40 and <b>2.06× at 100</b>. What the last column
    /// really shows is that the whole-mesh accumulation keeps growing with the pass count
    /// (124 → 322 → 623) while the restricted one nearly <b>flattens</b> (123 → 285 → 302):
    /// before this, face-aligned projection paid O(faces) every pass forever, so a converged
    /// mesh cost exactly as much per extra pass as an unconverged one. It is the only stage
    /// that did not compose with the scheduler, and now it does. Against the plain sweep the
    /// end-to-end figure at 100 passes is 3.07×.
    /// </para>
    /// <para>
    /// The restriction is <b>bit-identical</b>, not approximate, and is asserted so in
    /// <c>RemesherTests</c>: the accumulation keeps its ascending face scan and skips only the
    /// projection query, so every surviving vertex sums its own incident faces in exactly the
    /// order the whole-mesh walk gave it. Gathering the incident faces into a list instead
    /// needs a sort to restore that order and measured no faster.
    /// </para>
    /// </summary>
    [Fact]
    public void FaceAlignedAccumulationCost()
    {
        if (!Enabled)
            return;

        var mesh = MeshPrimitives.UvSphere(1.0, 48, 32).Triangulated();
        var target = new MeshProjectionTarget(mesh);
        double edge = 0.08;

        RemeshOptions FaceAligned(int passes) => new(edge)
        {
            Iterations = passes,
            FeatureAngleDegrees = 0,
            Projection = RemeshProjection.FaceAligned,
            ProjectionTarget = target,
        };

        var warm = Stopwatch.StartNew();
        do
        {
            Remesher.Remesh(mesh, FaceAligned(2));
        }
        while (warm.ElapsedMilliseconds < 1500);

        output.WriteLine($"input: {mesh.FaceCount} faces, target edge {edge}");
        output.WriteLine("passes | scheduling | accumulation |    ms | faces");

        foreach (int passes in new[] { 12, 40, 100 })
        {
            foreach (var (scheduling, accumulation, options) in new (string, string, RemeshOptions)[]
            {
                ("sweep", "whole mesh", FaceAligned(passes)),
                ("queue", "whole mesh", FaceAligned(passes) with
                {
                    Scheduling = RemeshScheduling.Queue, AccumulateOverEveryFace = true,
                }),
                ("queue", "incident", FaceAligned(passes) with { Scheduling = RemeshScheduling.Queue }),
            })
            {
                double ms = BestOf(() => Remesher.Remesh(mesh, options), 5, out var result);
                output.WriteLine(
                    $"{passes,6} | {scheduling,-10} | {accumulation,-12} | {ms,5:F0} | {result.Mesh.FaceCount,5}");
            }
        }
    }

    [Fact]
    public void SchedulingCostAndConvergence()
    {
        if (!Enabled)
            return;

        var mesh = Subject();
        var target = new MeshProjectionTarget(mesh);
        double edge = 0.05;

        var warm = Stopwatch.StartNew();
        do
        {
            Remesher.Remesh(mesh, Base(edge) with { Iterations = 2, ProjectionTarget = target });
        }
        while (warm.ElapsedMilliseconds < 1500);

        output.WriteLine($"input: {mesh.FaceCount} faces, target edge {edge}");
        output.WriteLine("passes | scheduling           |    ms | faces | out of band | splits/collapses/flips");

        foreach (int passes in new[] { 12, 40 })
        {
            foreach (var (name, options) in new (string, RemeshOptions)[]
            {
                ("sweep", Base(edge) with { Iterations = passes, ProjectionTarget = target }),
                ("sweep + fast split", Base(edge) with
                {
                    Iterations = passes, ProjectionTarget = target, FastSplitPasses = 4,
                }),
                ("queue", Base(edge) with
                {
                    Iterations = passes, ProjectionTarget = target, Scheduling = RemeshScheduling.Queue,
                }),
                ("queue + fast split", Base(edge) with
                {
                    Iterations = passes, ProjectionTarget = target,
                    Scheduling = RemeshScheduling.Queue, FastSplitPasses = 4,
                }),
            })
            {
                double ms = BestOf(() => Remesher.Remesh(mesh, options), 5, out var result);
                output.WriteLine(
                    $"{passes,6} | {name,-20} | {ms,5:F0} | {result.Mesh.FaceCount,5} | " +
                    $"{OutOfBand(result.Mesh, edge),11:P1} | " +
                    $"{result.Splits}/{result.Collapses}/{result.Flips}");
            }
        }
    }
}
