using System.Diagnostics;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// What the non-manifold <b>vertex</b> check costs <see cref="HalfEdgeMesh.Build"/>, as a
/// committed measurement rather than a remembered number. Inert unless <c>ENGRCAD_BENCH</c>
/// is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Mesh.Tests -c Release --filter FullyQualifiedName~HalfEdgeBuildBenchmark -l "console;verbosity=detailed"
/// </code>
/// <para>
/// <b>Build is on the hot path of every polygonization and every boolean</b> (a resolution-256
/// Surface Nets grid builds a 129 268-vertex mesh, and the exact mesh boolean re-builds both
/// operands per stage), so the question "check it or document it" is a cost question and this
/// is the answer. The check is the vertex-fan walk in
/// <see cref="HalfEdgeMesh.VertexFanTotal"/> — the SAME method <c>Build</c> calls, timed
/// standalone rather than transcribed, per this project's rule that a benchmark must not
/// measure a second copy of the thing it is judging.
/// </para>
/// <para>
/// Reference machine (win-x64, .NET 10.0.302, Release, otherwise idle):
/// </para>
/// <code>
/// fixture                       vertices   half-edges   build ms   check ms   share
/// UvSphere(1, 512, 254) quads    129 538      518 144       33.0       2.10    6.4%
/// UvSphere(1, 512, 254) tris     129 538    1 036 288       58.9       4.42    7.5%
/// UvSphere(1, 128, 96)  quads      12 290       49 152        2.5       0.15    6.1%
/// </code>
/// <para>
/// So the check is <b>6–8% of a build</b>, i.e. under 3% of a whole polygonization once the
/// grid walk is counted (assembly is 15–18% of a res-256 <c>Polygonize</c>). It is one pass of
/// pointer chasing over arrays the build has just written, with no allocation and no hashing,
/// and it closes the last structural hole in the manifold contract — so it is paid
/// unconditionally rather than being an option a caller has to know to switch on.
/// </para>
/// </summary>
public class HalfEdgeBuildBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    private static double BestOf(Action run, int trials)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < trials; i++)
        {
            var watch = Stopwatch.StartNew();
            run();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void VertexManifoldCheckCost()
    {
        if (!Enabled)
            return;

        var fixtures = new (string Name, HalfEdgeMesh Mesh)[]
        {
            ("UvSphere(1, 512, 254) quads", MeshPrimitives.UvSphere(1.0, 512, 254)),
            ("UvSphere(1, 512, 254) tris", MeshPrimitives.UvSphere(1.0, 512, 254).Triangulated()),
            ("UvSphere(1, 128, 96) quads", MeshPrimitives.UvSphere(1.0, 128, 96)),
        };

        output.WriteLine("fixture                        vertices   half-edges   build ms   check ms   share");
        foreach (var (name, mesh) in fixtures)
        {
            var (positions, faces) = mesh.ToIndexed();

            // Warm-up budget, not a warm-up COUNT: JIT tiering is promoted on a wall clock.
            var warm = Stopwatch.StartNew();
            do
            {
                HalfEdgeMesh.Build(positions, faces);
                mesh.VertexFanTotal();
            }
            while (warm.ElapsedMilliseconds < 1500);

            // Interleaved in ONE process against the production routine: the check timed here
            // is literally the call Build makes, so the ratio cannot be a measurement of two
            // differently-written baselines (the recorded delegate trap).
            double build = double.PositiveInfinity, check = double.PositiveInfinity;
            for (int i = 0; i < 5; i++)
            {
                build = Math.Min(build, BestOf(() => HalfEdgeMesh.Build(positions, faces), 1));
                check = Math.Min(check, BestOf(() => mesh.VertexFanTotal(), 1));
            }

            output.WriteLine(
                $"{name,-30} {mesh.VertexCount,8}   {mesh.HalfEdgeCount,10}   {build,8:F1}   {check,8:F2}   " +
                $"{check / build,5:P1}");
        }
    }
}
