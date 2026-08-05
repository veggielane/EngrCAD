using EngrCAD.Core;
using EngrCAD.TestSupport;
using Xunit;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// The batch/SIMD path's contract: it must agree with the scalar
/// <see cref="Sdf.Evaluate(in Vector3d)"/> bit for bit (a silently divergent fast path is
/// worse than no fast path), it must handle every batch length including the ragged tail
/// past the last full SIMD register and past the deinterleave chunk boundary, and it must
/// not allocate per call.
/// </summary>
public class BatchEvaluationTests
{
    // ---- the catalogue under test ----

    private static readonly (string Name, Sdf Field)[] Catalogue = BuildCatalogue();

    private static (string, Sdf)[] BuildCatalogue()
    {
        var sphere = Sdf.Sphere(6);
        var box = Sdf.Box(8, 5, 3);
        var cylinder = Sdf.Cylinder(4, 10);
        var torus = Sdf.Torus(6, 2);
        var capsule = Sdf.Capsule((-5, 0, 0), (5, 2, 1), 2);
        var half = Sdf.HalfSpace((1, 2, 3), 4);

        return
        [
            ("sphere", sphere),
            ("box", box),
            ("box-cube", Sdf.Box(6, 6, 6)),
            ("cylinder", cylinder),
            ("cone-frustum", Sdf.Cone(6, 3, 10)),
            ("cone-apex", Sdf.Cone(6, 0, 10)),
            ("cone-inverted-apex", Sdf.Cone(0, 6, 10)),
            ("cone-equal-radii", Sdf.Cone(4, 4, 10)),
            ("torus", torus),
            ("capsule", capsule),
            ("capsule-axis-aligned", Sdf.Capsule((0, 0, -4), (0, 0, 4), 3)),
            ("halfspace", half),
            ("gyroid", Sdf.Gyroid(5, 1)),

            // The lattice families. Both are deliberately scalar in the batch path — the TPMS
            // for the transcendental reason the gyroid records, the strut lattices because
            // their fold and prune are data-dependent branches — so what these entries pin is
            // that the DEFAULT seam still reproduces the scalar path exactly through every
            // operator above them.
            ("tpms-schwarz-p", Sdf.TpmsSheet(TpmsKind.SchwarzP, 5, 1)),
            ("tpms-schwarz-d", Sdf.TpmsSheet(TpmsKind.SchwarzD, 5, 1)),
            ("tpms-neovius", Sdf.TpmsSheet(TpmsKind.Neovius, 5, 0.6)),
            ("tpms-iwp", Sdf.TpmsSheet(TpmsKind.IwP, 5, 0.6)),
            ("tpms-lidinoid", Sdf.TpmsSheet(TpmsKind.Lidinoid, 5, 0.4)),
            ("tpms-fischer-koch-s", Sdf.TpmsSheet(TpmsKind.FischerKochS, 5, 0.4)),
            ("tpms-split-p", Sdf.TpmsSheet(TpmsKind.SplitP, 5, 0.4)),
            ("tpms-solid-gyroid", Sdf.TpmsSolid(TpmsKind.Gyroid, 5)),
            ("tpms-solid-iwp", Sdf.TpmsSolid(TpmsKind.IwP, 5, 0.4)),
            ("tpms-compiled", Sdf.TpmsSheet(TpmsKind.Lidinoid, 5, 0.4).Compile()),
            ("strut-simple-cubic", Sdf.StrutLattice(StrutLatticeKind.SimpleCubic, 5, 1.2)),
            ("strut-bcc", Sdf.StrutLattice(StrutLatticeKind.BodyCentredCubic, 5, 1.2)),
            ("strut-fcc", Sdf.StrutLattice(StrutLatticeKind.FaceCentredCubic, 5, 1)),
            ("strut-octet", Sdf.StrutLattice(StrutLatticeKind.Octet, 5, 0.8)),
            ("strut-diamond", Sdf.StrutLattice(StrutLatticeKind.Diamond, 5, 0.8)),
            ("strut-kelvin", Sdf.StrutLattice(StrutLatticeKind.Kelvin, 5, 0.8)),

            ("union", sphere | box),
            ("intersection", sphere & box),
            ("difference", box - cylinder),
            ("smooth-union", sphere.SmoothUnion(box, 1.5)),
            ("smooth-union-zero-k", sphere.SmoothUnion(box, 0)),
            ("smooth-union-negative-k", sphere.SmoothUnion(box, -1)),
            ("smooth-intersection", sphere.SmoothIntersect(box, 1.5)),
            ("smooth-difference", box.SmoothSubtract(cylinder, 1.5)),

            ("offset-grow", box.Offset(1.25)),
            ("offset-shrink", box.Offset(-0.75)),
            ("shell", box.Shell(0.8)),
            ("translate", box.Translate((1.5, -2.25, 0.5))),
            ("rotate", box.Rotate(Quaterniond.FromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.7))),
            ("mirror", box.Translate((3, 0, 0)).Mirror((0.5, 0, 0), (1, 0, 0))),
            ("scale", box.Scale(1.75)),

            ("nary-union", Sdf.Union(sphere, box, cylinder, torus, capsule)),
            ("nary-intersection", Sdf.Intersection(sphere, box, half)),
            ("nary-smooth-union", Sdf.SmoothUnion([sphere, box, cylinder, torus], 1.25)),
            ("blend-wyvill", Sdf.Blend(sphere, box, 1.5)),
            ("blend-exponential", Sdf.Blend(sphere, box, 1.5, Falloff.Exponential)),

            ("rounded-box", Sdf.RoundedBox(9, 7, 5, 1.25)),
            ("ellipsoid", Sdf.Ellipsoid(6, 3, 2)),
            ("ellipsoid-sphere", Sdf.Ellipsoid(4, 4, 4)),
            ("pyramid", Sdf.Pyramid(8, 10)),
            ("round-cone", Sdf.RoundCone(3, 1.2, 8)),
            ("link", Sdf.Link(5, 1.5, 3)),
            ("prism-3", Sdf.Prism(3, 5, 9)),
            ("prism-6", Sdf.Prism(6, 5, 9)),
            ("wedge", Sdf.Wedge(10, 8, 9, 3, 1)),
            ("wedge-sharp", Sdf.Wedge(10, 8, 9)),
            ("convex-polyhedron", Sdf.ConvexPolyhedron(
                [((1, 0, 0), 4), ((-1, 0, 0), 4), ((0, 1, 0), 4),
                 ((0, -1, 0), 4), ((0, 0, 1), 4), ((0, 0, -1), 4)])),

            ("twist", Sdf.Box(9, 5, 14).Twist(0.25)),
            ("bend", Sdf.Box(24, 6, 4).Bend(0.05)),
            ("taper", Sdf.Box(10, 10, 16).Taper(1, 0.35)),
            ("taper-widening", Sdf.Cylinder(4, 12).Taper(0.5, 1.8)),
            ("elongate", sphere.Elongate((4, 2, 0))),
            ("elongate-zero", sphere.Elongate((0, 0, 0))),
            ("displace", sphere.Displace(0.4, (2, 2, 2))),
            ("repeat-infinite-1d", Sdf.Sphere(1.2).Repeat((4, 0, 0))),
            ("repeat-infinite-3d", Sdf.Sphere(1.2).Repeat((4, 4, 4))),
            ("repeat-limited", Sdf.Sphere(1.2).Repeat((4, 4, 0), new Vector3i(3, 2, 1))),
            ("repeat-of-offcentre", Sdf.Sphere(0.6).Translate((0.9, 0, 0)).Repeat((4, 0, 0))),

            ("compiled-bracket", Bracket().Compile()),
            ("compiled-twist", Sdf.Box(9, 5, 14).Twist(0.25).Compile()),

            ("extruded-region", Sdf.ExtrudedRegion(new RoundedRectRegion(6, 4, 1), 5)),
            ("revolved-region", Sdf.RevolvedRegion(new RoundedRectRegion(6, 4, 1))),
            ("thread", Sdf.Thread(5, 4.2, 1.25, 0.15, 0.3, 12)),

            ("sampled-dense", Bracket().Sampled(0.9)),
            ("sampled-lazy", Bracket().Sampled(0.9, lazy: true)),

            ("bracket", Bracket()),
            ("deep-chain", DeepChain(24)),
            ("scalar-only-subclass", new ScalarOnlySdf(sphere)),
        ];
    }

    /// <summary>A realistic CSG tree: transforms over primitives, n-ary union, a smooth
    /// blend and a difference — the shape real models actually take.</summary>
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

    private static Sdf Field(string name) => Catalogue.First(c => c.Name == name).Field;

    private static Sdf DeepChain(int depth)
    {
        Sdf f = Sdf.Sphere(3);
        for (int i = 1; i <= depth; i++)
            f = f.Union(Sdf.Sphere(3).Translate((i * 0.7, i * 0.3, i * 0.11)));
        return f;
    }

    /// <summary>Exercises the default (non-overriding) <c>EvaluateBatch</c>: a node that
    /// only implements the scalar path must still compose correctly under batch.</summary>
    private sealed class ScalarOnlySdf(Sdf inner) : Sdf
    {
        public override double Evaluate(in Vector3d point) => inner.Evaluate(point) - 0.25;

        public override Aabb Bounds => inner.Bounds.Expanded(0.25);
    }

    /// <summary>Exact 2D distance to a rounded rectangle centered at the origin — a
    /// stand-in for EngrCAD.Modeling's <c>SketchRegion</c>, which this project cannot see.</summary>
    private sealed class RoundedRectRegion(double width, double height, double radius) : IPlanarRegion
    {
        public double SignedDistance(in Vector2d point)
        {
            double qx = Math.Abs(point.X) - (width / 2 - radius);
            double qy = Math.Abs(point.Y) - (height / 2 - radius);
            double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
            return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
        }

        public Aabb Bounds => new((-width / 2, -height / 2, 0), (width / 2, height / 2, 0));
    }

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (name, _) in Catalogue)
                data.Add(name);
            return data;
        }
    }

    // ---- point sets ----

    private static Vector3d[] RandomPoints(int count, int seed, double extent)
    {
        var rng = new Random(seed);
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
            points[i] = new Vector3d(
                (rng.NextDouble() * 2 - 1) * extent,
                (rng.NextDouble() * 2 - 1) * extent,
                (rng.NextDouble() * 2 - 1) * extent);
        return points;
    }

    /// <summary>
    /// Coordinates chosen to land exactly on the catalogue's features — radii, half-heights,
    /// corners, axes and the origin — where distances hit exact zeros, ties between the
    /// inside and outside branches, and clamp endpoints. Random points essentially never
    /// reach these, and they are precisely where a min/max or clamp rewrite could diverge.
    /// </summary>
    private static Vector3d[] StructuredPoints()
    {
        double[] coords = [0, 0.5, -0.5, 1, -1, 1.5, -1.5, 2, -2, 2.5, -2.5, 3, -3, 4, -4,
                           5, -5, 6, -6, 8, -8, 10, -10, 12, -12];
        var points = new List<Vector3d>(coords.Length * coords.Length * coords.Length);
        foreach (double x in coords)
            foreach (double y in coords)
                foreach (double z in coords)
                    points.Add(new Vector3d(x, y, z));
        return [.. points];
    }

    // ---- the exactness assertions ----

    /// <summary>
    /// Bit-identical, or both exactly zero. The signed-zero escape is the one documented
    /// deviation: <c>Vector.Min</c>/<c>Vector.Max</c> break a ±0 tie by operand position,
    /// <c>Math.Min</c>/<c>Math.Max</c> by sign — and ±0 compare equal, so no consumer of
    /// the field can tell them apart.
    /// </summary>
    private static void AssertSameValue(double expected, double actual, string context)
    {
        if (BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual))
            return;
        if (expected == 0 && actual == 0)
            return;
        Assert.Fail($"{context}: scalar {expected:R} (0x{BitConverter.DoubleToInt64Bits(expected):X16}) " +
                    $"!= batch {actual:R} (0x{BitConverter.DoubleToInt64Bits(actual):X16})");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Batch_MatchesScalar_BitForBit_OnRandomPoints(string name)
    {
        var sdf = Field(name);
        foreach (double extent in new[] { 2.0, 12.0, 60.0 })
        {
            var points = RandomPoints(4096, seed: 20260725 + (int)extent, extent);
            var batch = new double[points.Length];
            sdf.Evaluate(points, batch);
            for (int i = 0; i < points.Length; i++)
                AssertSameValue(sdf.Evaluate(points[i]), batch[i], $"{name} @ extent {extent} index {i} {points[i]}");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Batch_MatchesScalar_BitForBit_OnSurfacesAndEdges(string name)
    {
        var sdf = Field(name);
        var points = StructuredPoints();
        var batch = new double[points.Length];
        sdf.Evaluate(points, batch);
        for (int i = 0; i < points.Length; i++)
            AssertSameValue(sdf.Evaluate(points[i]), batch[i], $"{name} @ {points[i]}");
    }

    /// <summary>
    /// Every length from empty through several deinterleave chunks: the vector loop covers
    /// whole registers only, so the ragged tail (and the 1024-point chunk seam, and its
    /// ±1 neighbours) are exactly where an off-by-one would hide.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Batch_MatchesScalar_AtEveryLength(string name)
    {
        var sdf = Field(name);
        var points = RandomPoints(2100, seed: 4242, extent: 9);
        int[] lengths = [0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 63, 127, 255,
                         1023, 1024, 1025, 2047, 2048, 2049, 2100];
        foreach (int length in lengths)
        {
            var batch = new double[points.Length];
            sdf.Evaluate(points.AsSpan(0, length), batch.AsSpan(0, length));
            for (int i = 0; i < length; i++)
                AssertSameValue(sdf.Evaluate(points[i]), batch[i], $"{name} @ length {length} index {i}");
        }
    }

    [Fact]
    public void Batch_WritesOnlyThePointPrefix_WhenTheDistanceSpanIsLonger()
    {
        var sdf = Field("bracket");
        var points = RandomPoints(37, seed: 7, extent: 10);
        var distances = new double[64];
        Array.Fill(distances, double.NaN);
        sdf.Evaluate(points, distances);
        for (int i = 0; i < points.Length; i++)
            Assert.False(double.IsNaN(distances[i]));
        for (int i = points.Length; i < distances.Length; i++)
            Assert.True(double.IsNaN(distances[i]), $"slot {i} past the point count was overwritten");
    }

    [Fact]
    public void Batch_ThrowsWhenTheDistanceSpanIsTooShort()
    {
        var sdf = Sdf.Sphere(1);
        var points = new Vector3d[8];
        var distances = new double[4];
        Assert.Throws<ArgumentException>(() =>
        {
            var p = points; var d = distances;
            sdf.Evaluate(p, d);
        });
    }

    /// <summary>
    /// The performance mandate: scratch comes from <see cref="System.Buffers.ArrayPool{T}"/>
    /// and stack, never <c>new</c> per call — including for the multi-buffer operator and
    /// transform nodes, which are the ones that could regress this.
    /// </summary>
    [Fact]
    public void Batch_DoesNotAllocatePerCall()
    {
        var sdf = Field("bracket");
        var points = RandomPoints(4096, seed: 11, extent: 20);
        var distances = new double[points.Length];

        // The MINIMUM over several batches — a JIT/pool warm-up COUNT cannot settle
        // tiered compilation, which is promoted on a background queue, and a GC on a
        // neighbouring xUnit thread perturbs the reading (see AllocationProbe).
        long delta = AllocationProbe.SteadyStateBytes(() =>
        {
            for (int i = 0; i < 32; i++)
                sdf.Evaluate(points, distances);
        });

        Assert.Equal(0, delta);
    }

    /// <summary>The SoA seam is what operators forward to; a node that overrides it must
    /// be reached through the public AoS entry point (otherwise the fast path is dead code).</summary>
    [Fact]
    public void PublicBatchEntryPoint_DrivesTheStructureOfArraysSeam()
    {
        var spy = new SoaSpySdf(Sdf.Sphere(3));
        var wrapped = spy.Translate((1, 0, 0)) | Sdf.Box(2, 2, 2);
        var points = RandomPoints(100, seed: 3, extent: 5);
        var distances = new double[points.Length];
        wrapped.Evaluate(points, distances);

        Assert.Equal(0, spy.ScalarCalls);          // assert BEFORE the scalar cross-check below
        Assert.Equal(points.Length, spy.SoaPoints);
        for (int i = 0; i < points.Length; i++)
            AssertSameValue(wrapped.Evaluate(points[i]), distances[i], $"index {i}");
    }

    private sealed class SoaSpySdf(Sdf inner) : Sdf
    {
        public int ScalarCalls { get; private set; }
        public int SoaPoints { get; private set; }

        public override double Evaluate(in Vector3d point)
        {
            ScalarCalls++;
            return inner.Evaluate(point);
        }

        public override Aabb Bounds => inner.Bounds;

        protected internal override void EvaluateBatch(
            ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> distances)
        {
            SoaPoints += x.Length;
            inner.EvaluateBatch(x, y, z, distances);
        }
    }
}
