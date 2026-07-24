using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Frame3dTests
{
    private const double Precision = 1e-15;

    private static void AssertEqual(in Vector3d expected, in Vector3d actual, double precision = Precision)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    private static Vector3d RandomVector(Random random) =>
        new(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);

    /// <summary>A random valid frame with unit-scale origin (keeps 1e-15 assertions honest).</summary>
    private static Frame3d RandomFrame(Random random)
    {
        while (true)
        {
            var x = RandomVector(random);
            var y = RandomVector(random);
            if (x.Length < 0.1 || x.Cross(y).Length < 0.1)
                continue; // avoid near-degenerate axes; FromXY would accept but conditioning suffers
            return Frame3d.FromXY(RandomVector(random), x, y);
        }
    }

    // ---- construction ----

    [Fact]
    public void WorldXY_IsIdentity()
    {
        Assert.Equal(Vector3d.Zero, Frame3d.WorldXY.Origin);
        Assert.Equal(Vector3d.UnitX, Frame3d.WorldXY.X);
        Assert.Equal(Vector3d.UnitY, Frame3d.WorldXY.Y);
        Assert.Equal(Vector3d.UnitZ, Frame3d.WorldXY.Z);
        var p = new Vector3d(0.3, -0.7, 0.2);
        Assert.Equal(p, Frame3d.WorldXY.ToWorld(p));
        Assert.Equal(p, Frame3d.WorldXY.ToLocal(p));
    }

    [Fact]
    public void FromXY_OrthonormalizesNonOrthogonalInput()
    {
        var frame = Frame3d.FromXY(new Vector3d(1, 2, 3), new Vector3d(2, 0, 0), new Vector3d(1, 1, 0));
        AssertEqual(Vector3d.UnitX, frame.X);
        AssertEqual(Vector3d.UnitY, frame.Y);
        AssertEqual(Vector3d.UnitZ, frame.Z);

        var skew = Frame3d.FromXY(default, new Vector3d(0.3, -0.4, 0.5), new Vector3d(-0.2, 0.9, 0.1));
        Assert.Equal(1, skew.X.Length, Precision);
        Assert.Equal(1, skew.Y.Length, Precision);
        Assert.Equal(1, skew.Z.Length, Precision);
        Assert.Equal(0, skew.X.Dot(skew.Y), Precision);
        Assert.Equal(0, skew.X.Dot(skew.Z), Precision);
        Assert.Equal(0, skew.Y.Dot(skew.Z), Precision);
        Assert.Equal(skew.X.Cross(skew.Y), skew.Z); // Z = X × Y exactly, by construction
    }

    [Fact]
    public void FromXY_MatchesSketchPlaneGramSchmidtOrder()
    {
        // The historical normalization order (SketchPlane.At): normalize x first, then
        // orthogonalize y against the *unit* x. Locked bit-for-bit.
        var xAxis = new Vector3d(0.3, -0.4, 0.5);
        var yAxis = new Vector3d(-0.2, 0.9, 0.1);
        var frame = Frame3d.FromXY(default, xAxis, yAxis);
        var x = xAxis.Normalized();
        var y = (yAxis - x * yAxis.Dot(x)).Normalized();
        Assert.Equal(x, frame.X);
        Assert.Equal(y, frame.Y);
    }

    [Fact]
    public void FromXY_ThrowsOnDegenerateAxes()
    {
        Assert.Throws<ArgumentException>(() => Frame3d.FromXY(default, Vector3d.Zero, Vector3d.UnitY));
        Assert.Throws<ArgumentException>(() => Frame3d.FromXY(default, Vector3d.UnitX, Vector3d.Zero));
        Assert.Throws<ArgumentException>(() => Frame3d.FromXY(default, Vector3d.UnitX, new Vector3d(2, 0, 0)));
        Assert.Throws<ArgumentException>(() => Frame3d.FromXY(default, Vector3d.UnitX, new Vector3d(-3, 0, 0)));
    }

    [Fact]
    public void FromZX_MatchesStepAxis2Convention()
    {
        // AXIS2_PLACEMENT_3D: z primary, x hint orthogonalized against z.
        var zAxis = new Vector3d(0.1, 0.2, 0.9);
        var xHint = new Vector3d(1, 0.1, -0.2);
        var frame = Frame3d.FromZX(new Vector3d(4, 5, 6), zAxis, xHint);
        var z = zAxis.Normalized();
        var x = (xHint - z * z.Dot(xHint)).Normalized();
        Assert.Equal(z, frame.Z);
        Assert.Equal(x, frame.X);
        Assert.Equal(z.Cross(x), frame.Y);

        Assert.Throws<ArgumentException>(() => Frame3d.FromZX(default, Vector3d.Zero, Vector3d.UnitX));
        Assert.Throws<ArgumentException>(() => Frame3d.FromZX(default, Vector3d.UnitZ, new Vector3d(0, 0, 5)));
    }

    // ---- FromNormal: the locked arbitrary-perpendicular convention ----
    // X must be Vector3d.ArbitraryPerpendicular of the normalized normal (cross with the
    // least-aligned canonical axis) — the convention used for axis frames across the
    // codebase (MakeTorus radial, StepReader Axis2 default X, plane/sphere circles).
    // If any of these expectations move, a perpendicular convention changed somewhere:
    // rotated-bore tessellation cracked the last time two sites disagreed.

    public static TheoryData<double, double, double, Vector3d, Vector3d> FromNormalCases => new()
    {
        //  normal          expected X          expected Y
        { 1, 0, 0, new Vector3d(0, 0, 1), new Vector3d(0, -1, 0) },
        { -1, 0, 0, new Vector3d(0, 0, -1), new Vector3d(0, -1, 0) },
        { 0, 1, 0, new Vector3d(0, 0, -1), new Vector3d(-1, 0, 0) },
        { 0, -1, 0, new Vector3d(0, 0, 1), new Vector3d(-1, 0, 0) },
        { 0, 0, 1, new Vector3d(0, 1, 0), new Vector3d(-1, 0, 0) },
        { 0, 0, -1, new Vector3d(0, -1, 0), new Vector3d(-1, 0, 0) },
    };

    [Theory]
    [MemberData(nameof(FromNormalCases))]
    public void FromNormal_CanonicalDirections_AreLocked(
        double nx, double ny, double nz, Vector3d expectedX, Vector3d expectedY)
    {
        var normal = new Vector3d(nx, ny, nz);
        var frame = Frame3d.FromNormal(new Vector3d(1, 2, 3), normal);
        Assert.Equal(normal, frame.Z);
        Assert.Equal(expectedX, frame.X); // exact: canonical inputs give exact axes
        Assert.Equal(expectedY, frame.Y);
        Assert.Equal(new Vector3d(1, 2, 3), frame.Origin);
    }

    [Fact]
    public void FromNormal_SkewVector_DelegatesToArbitraryPerpendicularBitForBit()
    {
        var normal = new Vector3d(1, 2, 3);
        var frame = Frame3d.FromNormal(default, normal);
        var z = normal.Normalized();
        var x = z.ArbitraryPerpendicular(Tolerance.Default);
        Assert.Equal(z, frame.Z);
        Assert.Equal(x, frame.X);
        Assert.Equal(z.Cross(x), frame.Y);
    }

    // ---- maps ----

    [Fact]
    public void ToWorld_ToLocal_RoundTripAtFemtoPrecision()
    {
        var random = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            // Half-unit magnitudes: the double round-trip accumulates ~5 ulp of the
            // coordinate magnitude, so 1e-15 absolute is honest at this scale.
            var frame = RandomFrame(random);
            var p = RandomVector(random) * 0.5;
            AssertEqual(p, frame.ToLocal(frame.ToWorld(p)));
            AssertEqual(p, frame.ToWorld(frame.ToLocal(p)));

            var v = RandomVector(random) * 0.5;
            AssertEqual(v, frame.ToLocalVector(frame.ToWorldVector(v)));
            AssertEqual(v, frame.ToWorldVector(frame.ToLocalVector(v)));
        }
    }

    [Fact]
    public void VectorMaps_IgnoreTranslation()
    {
        var frame = Frame3d.FromNormal(new Vector3d(100, -50, 25), new Vector3d(1, 1, 1));
        var v = new Vector3d(0.5, -0.25, 0.75);
        AssertEqual(frame.ToWorld(v) - frame.ToWorld(Vector3d.Zero), frame.ToWorldVector(v), 1e-13);
        Assert.Equal(v.Length, frame.ToWorldVector(v).Length, 1e-15); // rigid: lengths preserved
    }

    [Fact]
    public void RayMaps_TransformOriginAsPointAndDirectionAsVector()
    {
        var random = new Random(7);
        var frame = RandomFrame(random);
        var ray = new Ray3d(new Vector3d(0.1, 0.2, 0.3), new Vector3d(-0.4, 0.5, 0.6));

        var world = frame.ToWorld(ray);
        AssertEqual(frame.ToWorld(ray.Origin), world.Origin);
        AssertEqual(frame.ToWorldVector(ray.Direction), world.Direction);

        var back = frame.ToLocal(world);
        AssertEqual(ray.Origin, back.Origin);
        AssertEqual(ray.Direction, back.Direction);

        // A point on the ray maps to a point on the mapped ray at the same parameter.
        AssertEqual(frame.ToWorld(ray.PointAt(0.75)), world.PointAt(0.75));
    }

    // ---- composition / inverse / matrix ----

    [Fact]
    public void Then_AppliesThisFirstThenOuter()
    {
        var random = new Random(11);
        var a = RandomFrame(random);
        var b = RandomFrame(random);
        var p = RandomVector(random);
        AssertEqual(b.ToWorld(a.ToWorld(p)), a.Then(b).ToWorld(p), 1e-14);
    }

    [Fact]
    public void Then_IsAssociative()
    {
        var random = new Random(13);
        for (int i = 0; i < 20; i++)
        {
            var a = RandomFrame(random);
            var b = RandomFrame(random);
            var c = RandomFrame(random);
            var left = a.Then(b).Then(c);
            var right = a.Then(b.Then(c));
            AssertEqual(left.Origin, right.Origin);
            AssertEqual(left.X, right.X);
            AssertEqual(left.Y, right.Y);
            AssertEqual(left.Z, right.Z);
        }
    }

    [Fact]
    public void Inverse_ComposesToIdentity()
    {
        var random = new Random(17);
        for (int i = 0; i < 20; i++)
        {
            var frame = RandomFrame(random);
            var identity = frame.Then(frame.Inverse());
            AssertEqual(Vector3d.Zero, identity.Origin);
            AssertEqual(Vector3d.UnitX, identity.X);
            AssertEqual(Vector3d.UnitY, identity.Y);
            AssertEqual(Vector3d.UnitZ, identity.Z);

            var other = frame.Inverse().Then(frame);
            AssertEqual(Vector3d.Zero, other.Origin);
            AssertEqual(Vector3d.UnitX, other.X);

            var p = RandomVector(random);
            AssertEqual(frame.ToLocal(p), frame.Inverse().ToWorld(p));
        }
    }

    [Fact]
    public void ToMatrix_AgreesWithToWorldPointForPoint()
    {
        var random = new Random(19);
        for (int i = 0; i < 20; i++)
        {
            var frame = RandomFrame(random);
            var m = frame.ToMatrix();
            var p = RandomVector(random);
            AssertEqual(frame.ToWorld(p), m.TransformPoint(p));
            AssertEqual(frame.ToWorldVector(p), m.TransformVector(p));
        }
    }

    [Fact]
    public void ToMatrix_ComposesLikeThen()
    {
        var random = new Random(23);
        var a = RandomFrame(random);
        var b = RandomFrame(random);
        var p = RandomVector(random);
        // Column-vector convention: b*a applies a first — matching a.Then(b).
        AssertEqual((b.ToMatrix() * a.ToMatrix()).TransformPoint(p), a.Then(b).ToWorld(p), 1e-14);
    }

    // ---- renormalization ----

    [Fact]
    public void Renormalized_RestoresOrthonormalityAfterIteratedDrift()
    {
        // Simulate iterated-frame drift: compose thousands of small rotations.
        var step = Frame3d.FromXY(
            Vector3d.Zero,
            new Vector3d(Math.Cos(0.01), Math.Sin(0.01), 0),
            new Vector3d(-Math.Sin(0.01), Math.Cos(0.01), 0.0003));
        var frame = Frame3d.FromNormal(new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 2, 3));
        for (int i = 0; i < 20000; i++)
            frame = frame.Then(step);

        var renormalized = frame.Renormalized();
        Assert.Equal(1, renormalized.X.Length, 1e-15);
        Assert.Equal(1, renormalized.Y.Length, 1e-15);
        Assert.Equal(1, renormalized.Z.Length, 1e-15);
        Assert.Equal(0, renormalized.X.Dot(renormalized.Y), 1e-15);
        Assert.Equal(renormalized.X.Cross(renormalized.Y), renormalized.Z);

        // Drift is small, so renormalization must not move the frame materially.
        Assert.True(renormalized.X.DistanceTo(frame.X) < 1e-9);
        Assert.True(renormalized.Y.DistanceTo(frame.Y) < 1e-9);
        Assert.Equal(frame.Origin, renormalized.Origin);
    }

    // ---- equality ----

    [Fact]
    public void Equality_IsBitwise_AreEqualIsTolerant()
    {
        var a = Frame3d.FromNormal(new Vector3d(1, 2, 3), new Vector3d(0, 0, 1));
        var b = Frame3d.FromNormal(new Vector3d(1, 2, 3), new Vector3d(0, 0, 1));
        Assert.True(a == b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var c = Frame3d.FromNormal(new Vector3d(1, 2, 3 + 1e-12), new Vector3d(0, 0, 1));
        Assert.True(a != c);
        Assert.True(a.AreEqual(c, Tolerance.Default));
        Assert.False(a.AreEqual(c, new Tolerance(1e-13, 1e-13)));
    }
}
