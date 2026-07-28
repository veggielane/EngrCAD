using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The seed-selection rule behind <see cref="ExtrudedSurface"/>'s and
/// <see cref="RevolvedSurface"/>'s one-dimensional inverse evaluation: refine from every
/// LOCAL MINIMUM of the sampled residual and its two neighbours, not from the single best
/// seed. <see cref="SweptSurface"/> learned this the hard way (a sliver profile hides two
/// branches inside one seed interval, and single-seed refinement returns the MIRRORED
/// parameter); these two inherited the same weakness from the generic base implementation.
///
/// <para>Each test measures the shipped surface against a deliberately single-seeded
/// reference built from the same public geometry, so the claim is "strictly better on
/// hostile input, never worse anywhere" rather than a recorded number that would rot.</para>
/// </summary>
public class SweptSeedSelectionTests
{
    /// <summary>
    /// A generator with far more features than the 17-sample seed table can resolve: a vase
    /// profile with <paramref name="bumps"/> waves. Every wave is a local minimum of the
    /// residual for points near it, so a single seed is repeatedly the wrong one.
    /// </summary>
    private static NurbsCurve WavyGenerator(int bumps)
    {
        var points = new Vector3d[8 * bumps];
        for (int i = 0; i < points.Length; i++)
        {
            double t = (double)i / (points.Length - 1);
            points[i] = new Vector3d(4 + 0.6 * Math.Sin(2 * Math.PI * bumps * t), 0, 10 * t);
        }
        return NurbsCurve.InterpolatePoints(points);
    }

    /// <summary>Fraction of a systematic (u, v) grid whose surface points round-trip.</summary>
    private static (int Accepted, int Total) Acceptance(Surface surface, Surface probe, int steps = 41)
    {
        int accepted = 0, total = 0;
        for (int i = 0; i <= steps; i++)
        {
            for (int j = 0; j <= steps; j++)
            {
                var point = surface.PointAt(
                    surface.DomainU.ParameterAt((double)i / steps),
                    surface.DomainV.ParameterAt((double)j / steps));
                total++;
                if (probe.TryProjectPoint(point, out var uv, 1e-6)
                    && probe.PointAt(uv.X, uv.Y).AreEqual(point, new Tolerance(1e-6, 1e-6)))
                    accepted++;
            }
        }
        return (accepted, total);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void Extruded_MultiSeedBeatsSingleSeed_OnAGeneratorTheSeedTableCannotResolve(int bumps)
    {
        var generator = WavyGenerator(bumps);
        var surface = new ExtrudedSurface(generator, new Vector3d(0, 5, 0));
        var singleSeed = new SingleSeedExtrusion(generator, new Vector3d(0, 5, 0));

        var shipped = Acceptance(surface, surface);
        var reference = Acceptance(surface, singleSeed);
        Assert.True(shipped.Accepted > reference.Accepted,
            $"{bumps} bumps: multi-seed {shipped.Accepted}/{shipped.Total} vs single-seed {reference.Accepted}");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    public void Revolved_MultiSeedBeatsSingleSeed_OnAGeneratorTheSeedTableCannotResolve(int bumps)
    {
        var generator = WavyGenerator(bumps);
        var surface = new RevolvedSurface(generator, Vector3d.Zero, Vector3d.UnitZ);
        var singleSeed = new SingleSeedRevolve(generator, Vector3d.Zero, Vector3d.UnitZ);

        var shipped = Acceptance(surface, surface);
        var reference = Acceptance(surface, singleSeed);
        Assert.True(shipped.Accepted > reference.Accepted,
            $"{bumps} bumps: multi-seed {shipped.Accepted}/{shipped.Total} vs single-seed {reference.Accepted}");
    }

    [Fact]
    public void Extruded_ASliverProjectionRoundTripsCompletely()
    {
        // The swept surface's own failure mode, transplanted: a circular generator extruded
        // along a direction nearly IN its own plane projects — perpendicular to that
        // direction, which is the only thing constraining u — into a sliver whose two sides
        // sit closer together than one seed interval. Single-seed refinement descends to the
        // mirrored parameter and the projection is REJECTED (v cannot rescue a wrong u), so
        // the symptom here is lost queries rather than wrong answers.
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.0);
        foreach (double squash in new[] { 0.2, 0.01, 0.002 })
        {
            var direction = new Vector3d(3, 0, 3 * squash);
            var surface = new ExtrudedSurface(circle, direction);
            var result = Acceptance(surface, surface);
            Assert.Equal(result.Total, result.Accepted);
        }
    }

    [Fact]
    public void NeitherOverrideEverDoesWorseThanTheGenericGridSearch()
    {
        // The base class scans a 17x17 (u, v) grid and Gauss-Newtons in 2D. Wherever it
        // succeeds, the 1D reduction must succeed and land on the same surface point; it is
        // allowed to succeed where the base does not, which on these shapes it does often.
        var generator = WavyGenerator(12);
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.0);
        Surface[] surfaces =
        [
            new ExtrudedSurface(generator, new Vector3d(0, 5, 0)),
            new ExtrudedSurface(circle, new Vector3d(3, 0, 0.03)),
            new RevolvedSurface(generator, Vector3d.Zero, Vector3d.UnitZ),
            new RevolvedSurface(new Circle3d(new Vector3d(4, 0, 0), Vector3d.UnitX, Vector3d.UnitZ, 0.05),
                Vector3d.Zero, Vector3d.UnitZ),
        ];

        foreach (var surface in surfaces)
        {
            var generic = new GenericProjection(surface);
            for (int i = 0; i <= 20; i++)
            {
                for (int j = 0; j <= 20; j++)
                {
                    double u = surface.DomainU.ParameterAt(i / 20.0);
                    double v = surface.DomainV.ParameterAt(j / 20.0);
                    var point = surface.PointAt(u, v);
                    if (!generic.TryProjectPoint(point, out var genericUv, 1e-6))
                        continue;
                    Assert.True(surface.TryProjectPoint(point, out var fastUv, 1e-6),
                        $"{surface.GetType().Name}: the override rejected a point the base accepted (u={u} v={v})");
                    Assert.True(surface.PointAt(fastUv.X, fastUv.Y)
                        .AreEqual(surface.PointAt(genericUv.X, genericUv.Y), new Tolerance(2e-6, 2e-6)));
                }
            }
        }
    }

    /// <summary>Forces the generic base implementation by hiding the override.</summary>
    private sealed class GenericProjection(Surface inner) : Surface
    {
        public override Interval DomainU => inner.DomainU;
        public override Interval DomainV => inner.DomainV;
        public override Vector3d PointAt(double u, double v) => inner.PointAt(u, v);
    }

    /// <summary>
    /// <see cref="ExtrudedSurface"/>'s inverse evaluation as it was before the multi-seed
    /// rule: the same 1D reduction, the same Newton iteration, seeded ONLY from the single
    /// best sample. Written against public geometry so the comparison stays honest.
    /// </summary>
    private sealed class SingleSeedExtrusion(Curve3d generator, Vector3d direction) : Surface
    {
        public override Interval DomainU => generator.Domain;
        public override Interval DomainV => Interval.Unit;
        public override Vector3d PointAt(double u, double v) => generator.PointAt(u) + direction * v;

        public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
        {
            double axisLengthSquared = direction.LengthSquared;
            var axis = direction;
            Vector3d Perpendicular(in Vector3d v) => v - axis * (v.Dot(axis) / axisLengthSquared);

            var target = point;
            var domain = generator.Domain;
            const int seeds = 16;
            double best = double.PositiveInfinity, parameter = domain.Start;
            for (int i = 0; i <= seeds; i++)
            {
                double u = domain.ParameterAt((double)i / seeds);
                double squared = Perpendicular(generator.PointAt(u) - target).LengthSquared;
                if (squared < best)
                {
                    best = squared;
                    parameter = u;
                }
            }
            for (int iteration = 0; iteration < 25; iteration++)
            {
                var residual = Perpendicular(generator.PointAt(parameter) - target);
                if (residual.Length < tolerance)
                    break;
                var slope = Perpendicular(generator.DerivativeAt(parameter));
                double denominator = slope.LengthSquared;
                if (denominator < 1e-30)
                    break;
                double next = domain.Clamp(parameter - slope.Dot(residual) / denominator);
                if (Math.Abs(next - parameter) <= domain.Length * 1e-15)
                {
                    parameter = next;
                    break;
                }
                parameter = next;
            }
            double along = (target - generator.PointAt(parameter)).Dot(direction) / axisLengthSquared;
            uv = new Vector2d(parameter, Interval.Unit.Clamp(along));
            return (PointAt(uv.X, uv.Y) - target).Length < tolerance;
        }
    }

    /// <summary><see cref="RevolvedSurface"/>'s inverse evaluation, single-seeded.</summary>
    private sealed class SingleSeedRevolve(Curve3d generator, Vector3d axisOrigin, Vector3d axisDirection) : Surface
    {
        private readonly Vector3d _axis = axisDirection.Normalized();

        public override Interval DomainU => new(0, 2 * Math.PI);
        public override Interval DomainV => generator.Domain;

        public override Vector3d PointAt(double u, double v) =>
            axisOrigin + Quaterniond.FromAxisAngle(_axis, u).Rotate(generator.PointAt(v) - axisOrigin);

        private (double R, double Z, double DR, double DZ) Profile(double v)
        {
            var q = generator.PointAt(v) - axisOrigin;
            double z = q.Dot(_axis);
            var r = q - _axis * z;
            double length = r.Length;
            var slope = generator.DerivativeAt(v);
            double dz = slope.Dot(_axis);
            var dr = slope - _axis * dz;
            return (length, z, length > 0 ? r.Dot(dr) / length : 0, dz);
        }

        public override bool TryProjectPoint(in Vector3d point, out Vector2d uv, double tolerance = 1e-8)
        {
            uv = default;
            var offset = point - axisOrigin;
            double height = offset.Dot(_axis);
            var radial = offset - _axis * height;
            double radius = radial.Length;
            if (radius <= tolerance)
                return false;

            var domain = generator.Domain;
            const int seeds = 16;
            double best = double.PositiveInfinity, parameter = domain.Start;
            for (int i = 0; i <= seeds; i++)
            {
                double v = domain.ParameterAt((double)i / seeds);
                var (r, z, _, _) = Profile(v);
                double squared = (r - radius) * (r - radius) + (z - height) * (z - height);
                if (squared < best)
                {
                    best = squared;
                    parameter = v;
                }
            }
            for (int iteration = 0; iteration < 25; iteration++)
            {
                var (r, z, dr, dz) = Profile(parameter);
                double fr = r - radius, fz = z - height;
                if (Math.Sqrt(fr * fr + fz * fz) < tolerance)
                    break;
                double denominator = dr * dr + dz * dz;
                if (denominator < 1e-30)
                    break;
                double next = domain.Clamp(parameter - (dr * fr + dz * fz) / denominator);
                if (Math.Abs(next - parameter) <= domain.Length * 1e-15)
                {
                    parameter = next;
                    break;
                }
                parameter = next;
            }

            var generatorOffset = generator.PointAt(parameter) - axisOrigin;
            var generatorRadial = generatorOffset - _axis * generatorOffset.Dot(_axis);
            if (generatorRadial.LengthSquared <= 0)
                return false;
            double angle = Math.Atan2(generatorRadial.Cross(radial).Dot(_axis), generatorRadial.Dot(radial));
            if (angle < 0)
                angle += 2 * Math.PI;
            uv = new Vector2d(DomainU.Clamp(angle), parameter);
            return (PointAt(uv.X, uv.Y) - point).Length < tolerance;
        }
    }
}
