using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The T-junction seam zip's two thresholds are the epsilon ladder's 1e-7 seam tier
/// expressed RELATIVELY (a fraction of the soup's own extent). They used to be absolute,
/// which is wrong in both directions away from unit scale: a large model's genuine seam
/// vertices sit further off their coarse edge than an absolute 1e-7 and the crack stays
/// open, while a small model's distinct vertices sit closer than it and get zipped into
/// edges they have no business on.
/// </summary>
public class MeshWelderZipTests
{
    /// <summary>
    /// A coarse triangle spanning (0,0)–(2s,0) with two finer triangles on the other side
    /// of that segment, meeting at a vertex <paramref name="offLine"/> away from its
    /// midpoint. Returns the coarse triangle's loop after zipping.
    /// </summary>
    private static List<int> ZipAndReturnCoarseLoop(double s, double offLine)
    {
        List<Vector3d> positions =
        [
            new(0, 0, 0),          // 0
            new(2 * s, 0, 0),      // 1
            new(s, s, 0),          // 2 — apex of the coarse triangle
            new(s, offLine, 0),    // 3 — the fine side's mid vertex, nudged off the seam
            new(0, -s, 0),         // 4
        ];
        List<List<int>> faces =
        [
            [0, 1, 2], // coarse: its 0 -> 1 edge has no reverse partner
            [1, 3, 4],
            [3, 0, 4],
        ];

        MeshWelder.ZipSeams(positions, faces);
        return faces[0];
    }

    [Theory]
    [InlineData(1e-5)]
    [InlineData(1.0)]
    [InlineData(1e5)]
    public void AVertexOnTheSeam_IsInsertedAtEveryScale(double s)
    {
        // 1e-7 of the model, i.e. inside the seam tier however big the model is. Under the
        // old absolute threshold this failed at s = 1e5 (the offset is 1e-2 there).
        var loop = ZipAndReturnCoarseLoop(s, offLine: 1e-7 * s);

        Assert.Equal([0, 3, 1, 2], loop);
    }

    [Theory]
    [InlineData(1e-5)]
    [InlineData(1.0)]
    [InlineData(1e5)]
    public void AVertexClearlyOffTheSeam_IsNotInsertedAtAnyScale(double s)
    {
        // Two decades outside the tier. Under the old absolute threshold this was wrongly
        // zipped at s = 1e-5 (the offset is 1e-10 there, well under an absolute 1e-7).
        var loop = ZipAndReturnCoarseLoop(s, offLine: 1e-5 * s);

        Assert.Equal([0, 1, 2], loop);
    }

    [Fact]
    public void ADegenerateSoup_IsLeftAlone()
    {
        // Zero extent: there is no seam, and the relative thresholds would be zero.
        List<Vector3d> positions = [new(1, 1, 1), new(1, 1, 1), new(1, 1, 1)];
        List<List<int>> faces = [[0, 1, 2]];

        MeshWelder.ZipSeams(positions, faces);

        Assert.Equal([0, 1, 2], faces[0]);
    }
}
