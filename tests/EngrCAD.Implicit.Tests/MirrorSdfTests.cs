using EngrCAD.Core;
using EngrCAD.Implicit;
using Xunit;

namespace EngrCAD.Implicit.Tests;

public class MirrorSdfTests
{
    [Fact]
    public void Mirror_MatchesTheReflectedPrimitiveExactly()
    {
        // Sphere at (2, 0, 0) mirrored across the YZ plane == sphere at (−2, 0, 0).
        var mirrored = Sdf.Sphere(1).Translate((2, 0, 0)).Mirror(Vector3d.Zero, Vector3d.UnitX);
        var reference = Sdf.Sphere(1).Translate((-2, 0, 0));
        var random = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 8, (random.NextDouble() - 0.5) * 8, (random.NextDouble() - 0.5) * 8);
            Assert.Equal(reference.Evaluate(p), mirrored.Evaluate(p), 12);
        }
    }

    [Fact]
    public void Mirror_AcrossOffsetSlantedPlane_IsAnIsometry()
    {
        var source = Sdf.Box(2, 1, 0.5).Translate((1, 2, 3));
        var point = new Vector3d(0.5, -1, 2);
        var normal = new Vector3d(1, 1, 1);
        var mirrored = source.Mirror(point, normal);

        var n = normal.Normalized();
        var random = new Random(9);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 10, (random.NextDouble() - 0.5) * 10, (random.NextDouble() - 0.5) * 10);
            var reflected = p - n * (2 * n.Dot(p - point));
            Assert.Equal(source.Evaluate(reflected), mirrored.Evaluate(p), 12);
        }

        // Mirror twice = identity (up to the rounding of two reflections, ~1e-15).
        var twice = mirrored.Mirror(point, normal);
        for (int i = 0; i < 50; i++)
        {
            var p = new Vector3d(random.NextDouble() * 4, random.NextDouble() * 4, random.NextDouble() * 4);
            Assert.True(Math.Abs(source.Evaluate(p) - twice.Evaluate(p)) < 1e-9,
                $"double mirror drifted at {p}");
        }
    }

    [Fact]
    public void Mirror_BoundsAreReflected()
    {
        var mirrored = Sdf.Sphere(1).Translate((2, 0, 0)).Mirror(Vector3d.Zero, Vector3d.UnitX);
        var bounds = mirrored.Bounds;
        Assert.Equal(-3, bounds.Min.X, 12);
        Assert.Equal(-1, bounds.Max.X, 12);
        Assert.Equal(-1, bounds.Min.Y, 12);
        Assert.Equal(1, bounds.Max.Y, 12);
    }
}
