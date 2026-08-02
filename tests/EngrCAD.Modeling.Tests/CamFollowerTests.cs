using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// Roller-follower radius compensation and offset followers (CamFollower +
/// CamLaw.FromSketch(profile, follower) + CamLaw.PressureAngle). The fixture
/// throughout is the eccentric circle cam — a circle of radius a centred e off the
/// pivot — because every quantity has a closed form: the planar offset of a circle is
/// a circle, so the roller-centre law, the offset-follower law and the contact normal
/// are all exact expressions to hold the sampled law against.
/// </summary>
public class CamFollowerTests
{
    private const double A = 8;   // profile circle radius
    private const double E = 3;   // eccentricity (pivot inside: E < A)

    private static Sketch Profile() => Sketch.Circle(new Vector2d(E, 0), A);

    [Fact]
    public void RollerFollower_OnAnEccentricCircle_MatchesTheClosedForm()
    {
        // The offset of the circle at roller radius R is the circle of radius A + R
        // about the same centre, so the roller-centre distance along the radial line is
        // d(θ) = E·cosθ + √((A+R)² − E²·sin²θ) — the point-follower law with A → A + R.
        const double r = 2;
        var law = CamLaw.FromSketch(Profile(), CamFollower.Roller(r), samples: 720);

        for (double theta = 0; theta < 2 * Math.PI; theta += 0.37)
        {
            double s = Math.Sin(theta);
            double expected = E * Math.Cos(theta) + Math.Sqrt((A + r) * (A + r) - E * E * s * s);
            law.Evaluate(theta, out double lift, out double slope, out _);
            Assert.True(Math.Abs(lift - expected) < 1e-6, $"lift at {theta:g4}: {lift} vs {expected}");

            double c = Math.Cos(theta);
            double root = Math.Sqrt((A + r) * (A + r) - E * E * s * s);
            double expectedSlope = -E * s - E * E * s * c / root;
            Assert.True(Math.Abs(slope - expectedSlope) < 1e-4,
                $"slope at {theta:g4}: {slope} vs {expectedSlope}");
        }
    }

    [Fact]
    public void RollerCompensation_IsAPlanarOffsetNotARadialOne()
    {
        // The reason the feature exists: r(θ) + R is wrong by O(R·r′²/r²), worst where
        // the cam is steepest. On this fixture at θ = π/2 the radial shortcut reads
        // √(A²−E²) + R = 9.416 where the true roller centre sits at √((A+R)²−E²) =
        // 9.539 — an error of 0.12, three orders above the law's own fidelity. The
        // discriminating half of the assertion is that the SHORTCUT misses by that
        // much; without it, this test would pass a roller law wired to the shortcut.
        const double r = 2;
        var point = CamLaw.FromSketch(Profile());
        var roller = CamLaw.FromSketch(Profile(), CamFollower.Roller(r));

        double theta = Math.PI / 2;
        double truth = E * Math.Cos(theta) + Math.Sqrt((A + r) * (A + r) - E * E * Math.Sin(theta) * Math.Sin(theta));
        roller.Evaluate(theta, out double rollerLift, out _, out _);
        point.Evaluate(theta, out double pointLift, out _, out _);

        Assert.True(Math.Abs(rollerLift - truth) < 1e-6,
            $"roller law {rollerLift:g8} vs planar-offset truth {truth:g8}");
        Assert.True(Math.Abs(pointLift + r - truth) > 0.1,
            "the radial shortcut must measurably disagree here, or this fixture proves nothing");
    }

    [Fact]
    public void ARollerOfRadiusZero_IsExactlyThePointFollower()
    {
        // Bit-identical, not merely close: the radial path's march and bisection see
        // reach + 0.0 and an isolevel of 0.0, which change no bits.
        var point = CamLaw.FromSketch(Profile(), CamFollower.Point(0.3));
        var roller = CamLaw.FromSketch(Profile(), CamFollower.Roller(0, 0.3));
        for (double theta = 0; theta < 2 * Math.PI; theta += 0.17)
        {
            point.Evaluate(theta, out double a, out double va, out double ka);
            roller.Evaluate(theta, out double b, out double vb, out double kb);
            Assert.Equal(a, b);
            Assert.Equal(va, vb);
            Assert.Equal(ka, kb);
        }
    }

    [Fact]
    public void OffsetFollower_OnAnEccentricCircle_MatchesTheClosedForm()
    {
        // Travel line offset by o (positive = right of travel, so the line is
        // y = −o for the default travel angle 0): the follower centre solves
        // |p − c| = A + R with p = (s, −o), c = E(cosθ, sinθ), giving
        // s(θ) = E·cosθ + √((A+R)² − (o + E·sinθ)²).
        const double r = 1.5, o = 2.5;
        var law = CamLaw.FromSketch(Profile(), CamFollower.Roller(r, angle: 0, offset: o), samples: 720);

        for (double theta = 0; theta < 2 * Math.PI; theta += 0.31)
        {
            double reach = o + E * Math.Sin(theta);
            double expected = E * Math.Cos(theta) + Math.Sqrt((A + r) * (A + r) - reach * reach);
            law.Evaluate(theta, out double lift, out _, out _);
            Assert.True(Math.Abs(lift - expected) < 1e-6, $"lift at {theta:g4}: {lift} vs {expected}");
        }
    }

    [Fact]
    public void PressureAngle_MatchesTheContactNormalGeometry()
    {
        // Two derivations sharing only the physics: the formula reads the LAW
        // (tan φ = (slope − offset)/distance, from the instant-centre construction),
        // the oracle reads the GEOMETRY (the contact normal of a roller on a circle
        // passes through both centres, so cos|φ| = |t̂·(p − c)|/(A + R)). They agree
        // only if the instant-centre sign convention and the follower placement
        // convention are consistent — which is exactly what the offset exists to test.
        const double r = 1.5, o = 2.5;
        var follower = CamFollower.Roller(r, angle: 0, offset: o);
        var law = CamLaw.FromSketch(Profile(), follower, samples: 720);

        for (double theta = 0.05; theta < 2 * Math.PI; theta += 0.31)
        {
            law.Evaluate(theta, out double s, out _, out _);
            var p = new Vector2d(s, -o);
            var c = new Vector2d(E * Math.Cos(theta), E * Math.Sin(theta));
            double geometric = Math.Acos(Math.Min(1, Math.Abs(p.X - c.X) / (A + r)));

            double formula = Math.Abs(law.PressureAngle(theta, follower));
            Assert.True(Math.Abs(formula - geometric) < 1e-4,
                $"pressure angle at {theta:g4}: formula {formula:g6} vs geometry {geometric:g6}");
        }
    }

    [Fact]
    public void PressureAngle_OffsetReducesTheRiseSide()
    {
        // The number the offset exists to improve: on the rise (slope > 0, cam angle
        // counterclockwise) a positive offset must REDUCE the pressure angle relative
        // to the radial follower at the same cam angle.
        var radial = CamLaw.FromSketch(Profile(), CamFollower.Point(0));
        var offsetFollower = CamFollower.Point(0, offset: 2);
        var offset = CamLaw.FromSketch(Profile(), offsetFollower);

        // The eccentric circle rises over θ ∈ (π, 2π) (slope = −E·sinθ − … > 0 there).
        double theta = 4.5;
        radial.Evaluate(theta, out _, out double slope, out _);
        Assert.True(slope > 0, "fixture check: this angle must be on the rise");

        double plain = Math.Abs(radial.PressureAngle(theta, CamFollower.Point(0)));
        double improved = Math.Abs(offset.PressureAngle(theta, offsetFollower));
        Assert.True(improved < plain,
            $"a positive offset must reduce the rise-side pressure angle ({improved:g6} vs {plain:g6})");
    }

    [Fact]
    public void PressureAngle_ForARiseLawTakesTheBaseDistance()
    {
        // A catalogue rise law is zero-based, so the follower-centre distance is the
        // law's value plus the prime-circle term √(Rp² − o²); at the cycloidal
        // midpoint the slope is exactly 2·rise/span, so the answer is closed-form.
        const double rise = 10, primeRadius = 30;
        var law = CamLaw.Cycloidal(rise, Math.PI);
        double baseDistance = Math.Sqrt(primeRadius * primeRadius);

        double mid = Math.PI / 2;
        law.Evaluate(mid, out double lift, out double slope, out _);
        double expected = Math.Atan2(slope, lift + baseDistance);
        Assert.Equal(expected, law.PressureAngle(mid, CamFollower.Point(0), baseDistance), 12);
        Assert.Equal(2 * rise / Math.PI, slope, 9);

        // And a distance that is not positive is refused by name, not returned as NaN.
        Assert.Throws<InvalidOperationException>(() => law.PressureAngle(0, CamFollower.Point(0)));
    }

    [Fact]
    public void RollerLaw_DrivesAFollowerThroughTheSolver()
    {
        // The law is an ordinary CamLaw, so the coupling machinery is untouched: a
        // roller law drives a prismatic follower to the closed form.
        var rig = new Assembly("camshaft");
        var frame = rig.Add(new Part("frame", MeshPrimitives.Box(4, 2, 1)));
        var cam = rig.Add(new Part("cam", MeshPrimitives.Box(4, 2, 1)));
        var follower = rig.Add(new Part("follower", MeshPrimitives.Box(4, 2, 1)),
            Frame3d.FromXY((0, 20, 0), Vector3d.UnitX, Vector3d.UnitY));
        var z = Vector3d.UnitZ;
        var camPin = Joint.Revolute(
            MateGeometry.Axis(frame, (0, 0, 0), z), MateGeometry.Axis(cam, (0, 0, 0), z), "cam pin");
        var slide = Joint.Prismatic(
            MateGeometry.Axis(frame, (0, 20, 0), Vector3d.UnitY),
            MateGeometry.Axis(follower, (0, 0, 0), Vector3d.UnitY), "follower");
        var mechanism = new Mechanism(rig).Ground(frame).Add(camPin).Add(slide);

        const double r = 2;
        mechanism.Add(Coupling.Cam(camPin, slide, CamLaw.FromSketch(Profile(), CamFollower.Roller(r))));
        mechanism.SolveAt(MechanismDriver.Angle(camPin), 1.3);

        double D(double theta) =>
            E * Math.Cos(theta) + Math.Sqrt((A + r) * (A + r) - E * E * Math.Sin(theta) * Math.Sin(theta));
        double expected = D(1.3) - D(0);
        Assert.True(Math.Abs(slide.Displacement - expected) < 1e-6,
            $"follower at {slide.Displacement:g6} vs closed form {expected:g6}");
    }

    [Fact]
    public void Followers_RefuseBadGeometryByName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CamFollower.Roller(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CamFollower.Roller(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => CamFollower.Point(0, double.PositiveInfinity));

        // A travel line offset clean past the profile never meets material: refused
        // naming the line, not returned as a garbage lift.
        var wide = Assert.Throws<ArgumentException>(
            () => CamLaw.FromSketch(Profile(), CamFollower.Point(0, offset: 20)));
        Assert.Contains("travel line", wide.Message);
    }
}
