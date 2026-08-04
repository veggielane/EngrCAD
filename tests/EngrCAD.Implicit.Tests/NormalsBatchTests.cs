using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Implicit.Tests;

/// <summary>
/// <see cref="Sdf.Normals"/>: the batched gradient, which exists because a gradient costs
/// six evaluations and a Hermite consumer wants thousands of them.
/// <para>
/// Its contract is the batch evaluator's, verbatim — <b>bit-for-bit identical to the
/// scalar <see cref="Sdf.Normal"/></b> at the same epsilon — and for the same reason: the
/// probe coordinates are the same expressions, the batch seam is contractually
/// bit-identical to the scalar evaluator, and the difference and the normalization are the
/// same two operations in the same order. Anything weaker would be a fast path a caller
/// could not substitute.
/// </para>
/// </summary>
public class NormalsBatchTests
{
    private static readonly (string Name, Sdf Field)[] Catalogue =
    [
        ("sphere", Sdf.Sphere(6)),
        ("box", Sdf.Box(8, 5, 3)),
        ("cylinder", Sdf.Cylinder(4, 10)),
        ("torus", Sdf.Torus(6, 2)),
        ("cone", Sdf.Cone(6, 3, 10)),
        ("gyroid", Sdf.Gyroid(5, 1)),
        ("difference", Sdf.Box(8, 8, 8) - Sdf.Cylinder(2, 12)),
        ("smooth-union", Sdf.Sphere(4).SmoothUnion(Sdf.Box(6, 3, 3), 1.5)),
        ("rotated", Sdf.Box(8, 5, 3).Rotate(
            Quaterniond.FromAxisAngle(new Vector3d(0.3, 0.8, 0.5).Normalized(), 0.7))),
        ("scaled", Sdf.Sphere(3).Scale(2.5)),
    ];

    public static TheoryData<string> Fields
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (name, _) in Catalogue)
                data.Add(name);
            return data;
        }
    }

    private static Sdf Field(string name) => Catalogue.First(c => c.Name == name).Field;

    private static Vector3d[] Points(int count)
    {
        // A deterministic spread over and through the fields' extents, deliberately
        // including on-surface and on-feature points where a gradient is worst behaved.
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)count;
            points[i] = new Vector3d(
                12 * Math.Cos(11 * t) * t - 4,
                9 * Math.Sin(7 * t + 1) - 1.5,
                8 * Math.Cos(17 * t + 2) * (1 - t) + 2.5);
        }
        return points;
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void BatchedNormalsAreBitIdenticalToTheScalarOverload(string name)
    {
        var field = Field(name);
        var points = Points(2500);
        var normals = new Vector3d[points.Length];
        field.Normals(points, normals, 1e-5);

        for (int i = 0; i < points.Length; i++)
        {
            var expected = field.Normal(points[i], 1e-5);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected.X), BitConverter.DoubleToInt64Bits(normals[i].X));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected.Y), BitConverter.DoubleToInt64Bits(normals[i].Y));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected.Z), BitConverter.DoubleToInt64Bits(normals[i].Z));
        }
    }

    /// <summary>Every length around the six-probe chunk boundary, which is where an
    /// off-by-one in the packing would hide.</summary>
    [Fact]
    public void EveryBatchLengthAgrees()
    {
        var field = Field("difference");
        var all = Points(400);
        const int chunk = 1024 / 6;
        foreach (int length in new[] { 0, 1, 2, 5, 6, 7, chunk - 1, chunk, chunk + 1, 2 * chunk, 2 * chunk + 3 })
        {
            var points = all.AsSpan(0, Math.Min(length, all.Length));
            var normals = new Vector3d[points.Length];
            field.Normals(points, normals);
            for (int i = 0; i < points.Length; i++)
                Assert.Equal(field.Normal(points[i]), normals[i]);
        }
    }

    /// <summary>
    /// |grad| is 1 for an exact distance field and less inside a smooth blend, which is
    /// exactly the distinction the overload exists to report — a consumer converting a
    /// field value into a distance needs to know which it has.
    /// </summary>
    [Fact]
    public void TheGradientMagnitudeSeparatesExactFieldsFromLowerBounds()
    {
        var points = Points(600);
        var normals = new Vector3d[points.Length];
        var magnitudes = new double[points.Length];

        foreach (string name in new[] { "sphere", "box", "cylinder", "torus", "difference", "rotated" })
        {
            Field(name).Normals(points, normals, magnitudes, 1e-5);
            // An exact distance field has |grad| = 1 ALMOST everywhere — not everywhere:
            // it is undefined on the medial axis and at a crease, where a central
            // difference straddles two branches and reads short (measured worst 0.99959).
            // So the claim is a share plus a ceiling, which is what the property is.
            Assert.True(magnitudes.Count(m => Math.Abs(m - 1) < 1e-6) > 0.95 * magnitudes.Length, name);
            Assert.All(magnitudes, m => Assert.InRange(m, 0.99, 1.0 + 1e-6));
        }

        // A smooth union's field is the documented lower bound; inside the blend band its
        // gradient is measurably short of unit length.
        var blend = Field("smooth-union");
        blend.Normals(points, normals, magnitudes, 1e-5);
        Assert.Contains(magnitudes, m => m < 0.99);
        Assert.All(magnitudes, m => Assert.InRange(m, 0, 1.0 + 1e-6));
    }

    /// <summary>Asking for magnitudes must not change a single bit of the normals.</summary>
    [Fact]
    public void AskingForMagnitudesLeavesTheNormalsUntouched()
    {
        var field = Field("smooth-union");
        var points = Points(500);
        var withOut = new Vector3d[points.Length];
        var with = new Vector3d[points.Length];
        var magnitudes = new double[points.Length];

        field.Normals(points, withOut, 1e-5);
        field.Normals(points, with, magnitudes, 1e-5);
        Assert.Equal(withOut, with);
    }

    [Fact]
    public void ShortSpansAreRefusedByName()
    {
        var field = Field("sphere");
        var points = Points(10);
        Assert.Throws<ArgumentException>(() => field.Normals(points, new Vector3d[9]));
        Assert.Throws<ArgumentException>(() => field.Normals(points, new Vector3d[10], new double[9]));
    }
}
