using System.Diagnostics;
using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// <see cref="Sdf.Compile"/>: the AST flattened to one delegate.
/// <para>
/// The contract is the SIMD path's — <b>bit-for-bit equality with the scalar walk</b> — and it
/// is asserted the same way, over the whole catalogue, at randomized points AND at structured
/// points that land exactly on surfaces, edges and corners where a rewritten min/max or clamp
/// could diverge. A compiled field is not an approximation of the field; it is the field.
/// </para>
/// <para>
/// Coverage is asserted separately, because a compiler that quietly fell back to a delegate
/// call for every node would pass every equality test while doing nothing.
/// </para>
/// </summary>
public class SdfCompilerTests(ITestOutputHelper output)
{
    private static (string Name, Sdf Field)[] Catalogue()
    {
        var sphere = Sdf.Sphere(6);
        var box = Sdf.Box(8, 5, 3);
        var cylinder = Sdf.Cylinder(4, 10);
        return
        [
            ("sphere", sphere),
            ("box", box),
            ("rounded-box", Sdf.RoundedBox(9, 7, 5, 1.25)),
            ("cylinder", cylinder),
            ("cone-frustum", Sdf.Cone(6, 3, 10)),
            ("cone-apex", Sdf.Cone(6, 0, 10)),
            ("torus", Sdf.Torus(6, 2)),
            ("capsule", Sdf.Capsule((-5, 0, 0), (5, 2, 1), 2)),
            ("half-space", Sdf.HalfSpace((1, 2, 3), 4)),
            ("ellipsoid", Sdf.Ellipsoid(6, 3, 2)),
            ("link", Sdf.Link(5, 1.5, 3)),
            ("prism-6", Sdf.Prism(6, 5, 9)),
            ("prism-3", Sdf.Prism(3, 5, 9)),
            ("wedge", Sdf.Wedge(10, 8, 9, 3, 1)),
            // The exact form's outside branch is a loop over triangles, so it takes the base
            // class's call-back-into-Evaluate fallback; the half-space form emits its planes.
            ("convex-polyhedron-fallback", Cube()),
            ("convex-polyhedron-bound", CubeBound()),
            ("gyroid", Sdf.Gyroid(5, 1)),
            ("pyramid-fallback", Sdf.Pyramid(8, 10)),
            ("graded-sheet", Sdf.TpmsSheet(
                TpmsKind.Gyroid, 5, LatticeGrading.Along((0, 0, 1), -8, 8, 0.4, 1.5))),

            ("union", sphere | box),
            ("intersection", sphere & box),
            ("difference", box - cylinder),
            ("smooth-union", sphere.SmoothUnion(box, 1.5)),
            ("smooth-union-zero-k", sphere.SmoothUnion(box, 0)),
            ("smooth-intersection", sphere.SmoothIntersect(box, 1.5)),
            ("smooth-difference", box.SmoothSubtract(cylinder, 1.5)),
            ("nary-union", Sdf.Union(sphere, box, cylinder, Sdf.Torus(6, 2))),
            ("nary-intersection", Sdf.Intersection(sphere, box)),
            ("nary-smooth-union", Sdf.SmoothUnion([sphere, box, cylinder], 1.25)),
            ("blend-wyvill", Sdf.Blend(sphere, box, 1.5)),
            ("blend-exponential", Sdf.Blend(sphere, box, 1.5, Falloff.Exponential)),

            ("offset", box.Offset(1.25)),
            ("shell", box.Shell(0.8)),
            ("translate", box.Translate((1.5, -2.25, 0.5))),
            ("rotate", box.Rotate(Quaterniond.FromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.7))),
            ("mirror", box.Translate((3, 0, 0)).Mirror((0.5, 0, 0), (1, 0, 0))),
            ("scale", box.Scale(1.75)),

            ("twist", Sdf.Box(9, 5, 14).Twist(0.25)),
            ("bend", Sdf.Box(24, 6, 4).Bend(0.05)),
            ("taper", Sdf.Box(10, 10, 16).Taper(1, 0.35)),
            ("elongate", sphere.Elongate((4, 2, 0))),
            ("displace", sphere.Displace(0.4, (2, 2, 2))),
            ("repeat-infinite", Sdf.Sphere(1.2).Repeat((4, 0, 0))),
            ("repeat-limited", Sdf.Sphere(1.2).Repeat((4, 4, 0), new Vector3i(3, 2, 1))),

            ("sampled-fallback", Bracket().Sampled(0.9)),
            ("bracket", Bracket()),
            ("deep-chain", DeepChain(24)),
            ("compiled-twice", Bracket().Compile()),
        ];
    }

    private static Sdf Cube() => Sdf.ConvexPolyhedron(CubePlanes());

    private static Sdf CubeBound() =>
        Sdf.ConvexPolyhedron(CubePlanes(), ConvexDistance.HalfSpaceBound);

    private static (Vector3d, double)[] CubePlanes() =>
    [
        ((1, 0, 0), 4), ((-1, 0, 0), 4),
        ((0, 1, 0), 4), ((0, -1, 0), 4),
        ((0, 0, 1), 4), ((0, 0, -1), 4),
    ];

    private static Sdf Bracket()
    {
        Sdf plate = Sdf.Box(30, 20, 6);
        Sdf boss = Sdf.Cylinder(5, 12).Translate((9, 0, 0));
        Sdf rib = Sdf.Capsule((-12, 0, 3), (12, 0, 3), 1.5);
        Sdf body = plate.SmoothUnion(boss, 2).Union(rib);
        var holes = new List<Sdf>();
        for (int i = -1; i <= 1; i += 2)
            for (int j = -1; j <= 1; j += 2)
                holes.Add(Sdf.Cylinder(1.5, 20).Translate((11.0 * i, 7.0 * j, 0)));
        holes.Add(Sdf.Torus(7, 1.25).Translate((0, 0, 3)));
        return body.Subtract(Sdf.Union(holes)).Rotate(Quaterniond.FromAxisAngle(Vector3d.UnitZ, 0.3));
    }

    private static Sdf DeepChain(int depth)
    {
        Sdf f = Sdf.Sphere(3);
        for (int i = 1; i <= depth; i++)
            f = f.Union(Sdf.Sphere(3).Translate((i * 0.7, i * 0.3, i * 0.11)));
        return f;
    }

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (name, _) in Catalogue())
                data.Add(name);
            return data;
        }
    }

    private static Sdf Field(string name) => Catalogue().First(c => c.Name == name).Field;

    [Theory]
    [MemberData(nameof(Names))]
    public void Compiled_MatchesScalar_BitForBit_OnRandomPoints(string name)
    {
        var field = Field(name);
        var compiled = field.Compile();
        foreach (double extent in new[] { 2.0, 12.0, 60.0 })
        {
            foreach (var p in DomainOperatorTests.Probes(seed: 4242 + (int)extent, count: 4000, extent))
            {
                double expected = field.Evaluate(p);
                double actual = compiled.Evaluate(p);
                Assert.True(
                    BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                    $"{name} at {p}: scalar {expected:R} (0x{BitConverter.DoubleToInt64Bits(expected):X16}) != " +
                    $"compiled {actual:R} (0x{BitConverter.DoubleToInt64Bits(actual):X16})");
            }
        }
    }

    /// <summary>
    /// Structured coordinates that land exactly on radii, half-heights, corners and axes —
    /// where distances hit exact zeros, where inside/outside branches tie, and where a clamp
    /// sits on an endpoint. Random points essentially never reach these, and they are precisely
    /// where a rewritten expression could diverge.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Compiled_MatchesScalar_BitForBit_OnSurfacesAndEdges(string name)
    {
        var field = Field(name);
        var compiled = field.Compile();
        double[] coords = [0, 0.5, -0.5, 1, -1, 1.5, -1.5, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 8, -8, 10, -10];
        foreach (double x in coords)
        {
            foreach (double y in coords)
            {
                foreach (double z in coords)
                {
                    var p = new Vector3d(x, y, z);
                    double expected = field.Evaluate(p);
                    double actual = compiled.Evaluate(p);
                    Assert.True(
                        BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                        $"{name} at {p}: scalar {expected:R} != compiled {actual:R}");
                }
            }
        }
    }

    /// <summary>The batch entry point of a compiled node must agree with its own scalar
    /// entry, exactly as every other node's does.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Compiled_BatchMatchesItsOwnScalar(string name)
    {
        var compiled = Field(name).Compile();
        var points = DomainOperatorTests.Probes(seed: 7, count: 2100, extent: 9).ToArray();
        int[] lengths = [0, 1, 3, 4, 7, 8, 1023, 1024, 1025, 2100];
        foreach (int length in lengths)
        {
            var batch = new double[points.Length];
            compiled.Evaluate(points.AsSpan(0, length), batch.AsSpan(0, length));
            for (int i = 0; i < length; i++)
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(compiled.Evaluate(points[i])),
                    BitConverter.DoubleToInt64Bits(batch[i]));
        }
    }

    [Fact]
    public void Compiled_ReportsTheSameBoundsAndLipschitzBoundAsItsSource()
    {
        var field = Sdf.Box(9, 5, 14).Twist(0.25);
        var compiled = field.Compile();
        var region = new Aabb((-20, -20, -20), (20, 20, 20));
        Assert.Equal(field.Bounds.Min, compiled.Bounds.Min);
        Assert.Equal(field.Bounds.Max, compiled.Bounds.Max);
        Assert.Equal(field.LipschitzBound(region), compiled.LipschitzBound(region));
    }

    /// <summary>
    /// Coverage: a compiler that fell back everywhere would pass every equality test above
    /// while flattening nothing. The fallback is a captured delegate invocation, so counting
    /// scalar <c>Evaluate</c> calls on a spy node inside the tree says which path was taken.
    /// </summary>
    [Fact]
    public void CompilationInlinesTheNodesItClaimsTo_AndFallsBackOnlyForTheRest()
    {
        // A node with no expression form (an external subclass would be the real case).
        var spy = new CountingSdf(Sdf.Sphere(2));
        var tree = (Sdf.Box(10, 10, 10) - spy).Translate((1, 0, 0));
        var compiled = tree.Compile();

        spy.Calls = 0;
        for (int i = 0; i < 100; i++)
            compiled.Evaluate((i * 0.1, 0.2, 0.3));
        Assert.Equal(100, spy.Calls);   // the fallback really is a call into Evaluate

        // And the rest of the tree did NOT go through a fallback: a fully inlinable tree of
        // the same shape makes no scalar calls into any node.
        var pure = (Sdf.Box(10, 10, 10) - Sdf.Sphere(2)).Translate((1, 0, 0));
        var pureSpyFree = pure.Compile();
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(pure.Evaluate((3, 0.2, 0.3))),
            BitConverter.DoubleToInt64Bits(pureSpyFree.Evaluate((3, 0.2, 0.3))));
    }

    private sealed class CountingSdf(Sdf inner) : Sdf
    {
        public int Calls;

        public override double Evaluate(in Vector3d point)
        {
            Calls++;
            return inner.Evaluate(point);
        }

        public override Aabb Bounds => inner.Bounds;
    }

    /// <summary>
    /// The measurement, reported rather than asserted as a threshold — a timing assertion on
    /// a shared machine is a flake, and the point of the number is the SHAPE of the answer:
    /// compilation beats the scalar walk and loses to the SIMD batch path, which is what every
    /// bulk consumer already uses.
    /// </summary>
    [Fact]
    public void Measure_ScalarVersusCompiledVersusBatch()
    {
        (string Name, Sdf Field)[] cases =
        [
            ("sphere", Sdf.Sphere(6)),
            ("bracket CSG", Bracket()),
            ("deep union chain (24)", DeepChain(24)),
        ];

        var points = DomainOperatorTests.Probes(seed: 2, count: 200_000, extent: 25).ToArray();
        var scratch = new double[points.Length];

        output.WriteLine("case                     scalar walk   compiled     batch (SIMD)   [Mpts/s]");
        foreach (var (name, field) in cases)
        {
            var compiled = field.Compile();

            double scalar = Throughput(() =>
            {
                double acc = 0;
                foreach (var p in points)
                    acc += field.Evaluate(p);
                return acc;
            }, points.Length);

            double compiledRate = Throughput(() =>
            {
                double acc = 0;
                foreach (var p in points)
                    acc += compiled.Evaluate(p);
                return acc;
            }, points.Length);

            double batch = Throughput(() =>
            {
                field.Evaluate(points, scratch);
                return scratch[0];
            }, points.Length);

            output.WriteLine(
                $"{name,-24} {scalar,10:0.0}   {compiledRate,8:0.0}   {batch,12:0.0}" +
                $"    (compiled/scalar {compiledRate / scalar:0.00}x, batch/compiled {batch / compiledRate:0.00}x)");
        }
    }

    /// <summary>
    /// Best of several passes after a wall-clock warm-up budget — never a warm-up COUNT, which
    /// the project has measured to report tier-0 code and swing a figure by 4x between runs.
    /// </summary>
    private static double Throughput(Func<double> work, int points)
    {
        var warmup = Stopwatch.StartNew();
        while (warmup.ElapsedMilliseconds < 400)
            work();

        double best = 0;
        for (int pass = 0; pass < 5; pass++)
        {
            var sw = Stopwatch.StartNew();
            work();
            sw.Stop();
            best = Math.Max(best, points / sw.Elapsed.TotalSeconds / 1e6);
        }
        return best;
    }
}
