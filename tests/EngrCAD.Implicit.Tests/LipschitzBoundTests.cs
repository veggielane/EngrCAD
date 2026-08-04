using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// <see cref="Sdf.LipschitzBound"/> is a CLAIM about the field, so it is verified against the
/// field rather than against the algebra it was derived from: secants are measured over the
/// whole node catalogue and the reported bound must cover them.
/// <para>
/// The failure this exists to catch is not a wrong formula but a MISSING override. A wrapper
/// node that forgets to propagate silently reports 1 for a twisted subtree, the polygonizer's
/// cull then widens by too little, and geometry disappears with nothing to report it — the
/// exact shape of defect this repository has paid for whenever a shared rule was restated
/// instead of asked. So the catalogue below deliberately wraps a twist in every operator that
/// takes a child.
/// </para>
/// </summary>
public class LipschitzBoundTests(ITestOutputHelper output)
{
    /// <summary>
    /// A twisted core, so any node that fails to propagate reports 1 and is caught. The twist
    /// rate is large enough that its own factor is far from 1 (about 1.7 at the probe radius),
    /// which is what makes a missing propagation visible rather than marginal.
    /// </summary>
    private static Sdf TwistedCore() => Sdf.Box(9, 5, 14).Twist(0.25);

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

    private static (string Name, Sdf Field)[] Catalogue()
    {
        var core = TwistedCore();
        var plain = Sdf.Sphere(5);
        return
        [
            // The exact family: every one of these must report exactly 1.
            ("sphere", Sdf.Sphere(5)),
            ("box", Sdf.Box(8, 5, 3)),
            ("rounded-box", Sdf.RoundedBox(8, 6, 4, 1)),
            ("cylinder", Sdf.Cylinder(4, 10)),
            ("cone", Sdf.Cone(5, 2, 9)),
            ("torus", Sdf.Torus(6, 2)),
            ("capsule", Sdf.Capsule((-4, 0, 0), (4, 1, 1), 2)),
            ("round-cone", Sdf.RoundCone(3, 1.2, 8)),
            ("link", Sdf.Link(5, 1.5, 3)),
            ("pyramid", Sdf.Pyramid(8, 10)),
            ("prism-6", Sdf.Prism(6, 5, 9)),
            ("wedge", Sdf.Wedge(10, 8, 9, 3, 1)),
            ("half-space", Sdf.HalfSpace((1, 2, 3), 2)),
            ("gyroid", Sdf.Gyroid(5, 1)),

            // Every wrapper, over the twisted core: each must propagate.
            ("union", core | plain),
            ("union-reversed", plain | core),
            ("intersection", core & Sdf.Box(30, 30, 30)),
            ("difference", Sdf.Box(30, 30, 30) - core),
            ("difference-tool", core - Sdf.Sphere(2)),
            ("smooth-union", core.SmoothUnion(plain, 1.5)),
            ("smooth-intersection", core.SmoothIntersect(Sdf.Box(30, 30, 30), 1.5)),
            ("smooth-difference", core.SmoothSubtract(Sdf.Sphere(2), 1.5)),
            ("offset", core.Offset(0.7)),
            ("shell", core.Shell(0.8)),
            ("translate", core.Translate((3, -2, 1))),
            ("rotate", core.Rotate(Quaterniond.FromAxisAngle(new Vector3d(1, 1, 0).Normalized(), 0.6))),
            ("mirror", core.Mirror((1, 0, 0), (1, 0, 0))),
            ("scale", core.Scale(1.7)),
            ("nary-union", Sdf.Union(plain, core, Sdf.Sphere(3))),
            ("nary-intersection", Sdf.Intersection(core, Sdf.Box(40, 40, 40))),
            ("nary-smooth-union", Sdf.SmoothUnion([plain, core, Sdf.Sphere(3)], 1.2)),
            ("blend-wyvill", Sdf.Blend(core, plain, 1.5)),
            ("elongate", core.Elongate((2, 1, 0))),
            ("repeat-limited", Sdf.Sphere(1.2).Repeat((4, 0, 0), new Vector3i(3, 1, 1))),
            ("displace", core.Displace(0.3, (1.5, 0, 0))),
            ("compiled", core.Compile()),

            // The non-isometries themselves.
            ("twist", core),
            ("bend", Sdf.Box(24, 6, 4).Bend(0.05)),
            ("taper", Sdf.Box(10, 10, 16).Taper(1, 0.35)),
            ("twist-of-bend", Sdf.Box(20, 6, 12).Bend(0.04).Twist(0.15)),

            // The sampled grids, whose interpolation is measurably steeper than the source.
            ("sampled-dense", Sdf.Sphere(6).Sampled(0.6)),
            ("sampled-lazy", Sdf.Sphere(6).Sampled(0.6, lazy: true)),
        ];
    }

    private static Sdf Field(string name) => Catalogue().First(c => c.Name == name).Field;

    /// <summary>
    /// The measurement. A secant over a short chord bounds the local slope from below, so the
    /// worst one over many random chords is a lower bound on the true Lipschitz constant, and
    /// the reported bound must be at least that.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void ReportedBound_CoversTheMeasuredSecants(string name)
    {
        var field = Field(name);
        var region = new Aabb((-16, -16, -16), (16, 16, 16));
        double reported = field.LipschitzBound(region);
        Assert.True(reported >= 1, $"{name}: a bound below 1 is never right ({reported:R})");

        var rng = new Random(20260804);
        double worst = 0;
        Vector3d worstAt = default;
        for (int i = 0; i < 60000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 15,
                (rng.NextDouble() * 2 - 1) * 15,
                (rng.NextDouble() * 2 - 1) * 15);
            var step = new Vector3d(
                rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized() * 1e-5;
            double slope = Math.Abs(field.Evaluate(p + step) - field.Evaluate(p)) / step.Length;
            if (slope > worst)
            {
                worst = slope;
                worstAt = p;
            }
        }

        output.WriteLine($"{name,-22} reported {reported,8:0.####}   measured {worst,8:0.####}   at {worstAt}");
        // Round-off in a difference of two values of size ~30 over a chord of 1e-5 is a few
        // parts in 1e-11 of the slope, far below this slack; the slack exists for that and
        // nothing else.
        Assert.True(worst <= reported * (1 + 1e-6),
            $"{name}: measured slope {worst:R} at {worstAt} exceeds the reported bound {reported:R}");
    }

    /// <summary>
    /// The other half: a bound that is merely large is useless. Every field built only from
    /// isometries and value combinations must report EXACTLY 1, so the widening it causes
    /// downstream is exactly none and the existing cull stays bit-identical.
    /// </summary>
    [Theory]
    [InlineData("sphere")]
    [InlineData("box")]
    [InlineData("rounded-box")]
    [InlineData("cylinder")]
    [InlineData("cone")]
    [InlineData("torus")]
    [InlineData("capsule")]
    [InlineData("round-cone")]
    [InlineData("link")]
    [InlineData("pyramid")]
    [InlineData("prism-6")]
    [InlineData("wedge")]
    [InlineData("half-space")]
    [InlineData("gyroid")]
    public void ExactFields_ReportExactlyOne(string name)
    {
        double bound = Field(name).LipschitzBound(new Aabb((-16, -16, -16), (16, 16, 16)));
        Assert.Equal(1.0, bound);
    }

    /// <summary>
    /// The composition rule, stated as a value: a plain CSG tree over exact primitives is
    /// still exactly 1, so nothing that existed before the domain operators widens anything.
    /// </summary>
    [Fact]
    public void APlainCsgTree_ReportsExactlyOne()
    {
        Sdf plate = Sdf.Box(30, 20, 6);
        Sdf boss = Sdf.Cylinder(5, 12).Translate((9, 0, 0));
        Sdf rib = Sdf.Capsule((-12, 0, 3), (12, 0, 3), 1.5);
        var body = plate.SmoothUnion(boss, 2).Union(rib)
            .Subtract(Sdf.Union(Sdf.Cylinder(1.5, 20), Sdf.Torus(7, 1.25).Translate((0, 0, 3))))
            .Rotate(Quaterniond.FromAxisAngle(Vector3d.UnitZ, 0.3))
            .Shell(0.9)
            .Offset(0.2);

        Assert.Equal(1.0, body.LipschitzBound(new Aabb((-40, -40, -40), (40, 40, 40))));
    }

    /// <summary>
    /// The guard must be shown to FIRE. A wrapper that forgets to propagate is exactly the
    /// bug this file exists for, so one is built deliberately and asserted to be caught by the
    /// same measurement the real nodes pass.
    /// </summary>
    [Fact]
    public void AWrapperThatForgetsToPropagate_IsCaughtByTheSameMeasurement()
    {
        var forgetful = new ForgetfulWrapper(TwistedCore());
        var region = new Aabb((-16, -16, -16), (16, 16, 16));

        double reported = forgetful.LipschitzBound(region);
        Assert.Equal(1.0, reported);   // the defect: it claims the default

        var rng = new Random(20260804);
        double worst = 0;
        for (int i = 0; i < 60000; i++)
        {
            var p = new Vector3d(
                (rng.NextDouble() * 2 - 1) * 15,
                (rng.NextDouble() * 2 - 1) * 15,
                (rng.NextDouble() * 2 - 1) * 15);
            var step = new Vector3d(
                rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized() * 1e-5;
            worst = Math.Max(worst, Math.Abs(forgetful.Evaluate(p + step) - forgetful.Evaluate(p)) / step.Length);
        }

        Assert.True(worst > reported * (1 + 1e-6),
            $"the deliberately broken wrapper was NOT caught (measured {worst:R} vs reported {reported:R}) — " +
            "the probe is too weak to protect the real nodes");
    }

    /// <summary>A node that wraps a child and inherits the default bound — the mistake.</summary>
    private sealed class ForgetfulWrapper(Sdf inner) : Sdf
    {
        public override double Evaluate(in Vector3d point) => inner.Evaluate(point);

        public override Aabb Bounds => inner.Bounds;
    }

    /// <summary>
    /// A sampled grid is steeper than what went into it, and the factor is attained rather
    /// than merely permitted: baking <c>max(x, y, z)</c> — 1-Lipschitz — onto a unit cell gives
    /// an interpolant whose gradient at the corner is (1, 1, 1). Measured here so the √3 in
    /// <c>SampledGridSdf</c> is a fact about the field rather than a derivation nobody checked.
    /// </summary>
    [Fact]
    public void ASampledGrid_IsMeasurablySteeperThanItsSource()
    {
        var source = new ChebyshevField();
        Assert.Equal(1.0, source.LipschitzBound(source.Bounds));

        var grid = source.Sampled(1.0);
        double reported = grid.LipschitzBound(grid.Bounds);
        Assert.Equal(Math.Sqrt(3), reported, 12);

        // Near the cell CORNER where the three coordinates swap places: the interpolant there
        // is 1 − (1−x)(1−y)(1−z) over that cell, whose gradient at the corner is (1, 1, 1).
        // (Sampled over its own bounds expanded by one cell, so samples land on integers.)
        double worst = 0;
        var rng = new Random(5);
        var step = new Vector3d(1, 1, 1).Normalized() * 1e-6;
        for (int i = 0; i < 20000; i++)
        {
            var q = new Vector3d(
                rng.NextDouble() * 0.08, rng.NextDouble() * 0.08, rng.NextDouble() * 0.08);
            worst = Math.Max(worst, Math.Abs(grid.Evaluate(q + step) - grid.Evaluate(q)) / step.Length);
        }

        output.WriteLine($"trilinear interpolant of a 1-Lipschitz field: measured {worst:0.####} (√3 = {Math.Sqrt(3):0.####})");
        Assert.True(worst > 1.5, $"the interpolant measured only {worst:R}; the fixture is not reaching the corner");
        Assert.True(worst <= reported * (1 + 1e-6));
    }

    /// <summary>
    /// <c>max(x, y, z)</c> over a box: 1-Lipschitz (its gradient is a coordinate axis almost
    /// everywhere) and precisely the field whose trilinear interpolant attains √3.
    /// </summary>
    private sealed class ChebyshevField : Sdf
    {
        public override double Evaluate(in Vector3d p) => Math.Max(p.X, Math.Max(p.Y, p.Z));

        public override Aabb Bounds => new((-4, -4, -4), (4, 4, 4));
    }
}
