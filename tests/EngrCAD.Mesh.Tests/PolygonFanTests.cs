using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Mesh.Tests;

/// <summary>
/// The shared fan rule. Two things need locking: that the rule reads GEOMETRY rather than
/// corner order, and — the part that took a second pass to get right — that it does NOT
/// read round-off, because a great many grid cells have mathematically equal diagonals.
/// </summary>
public class PolygonFanTests(ITestOutputHelper output)
{
    [Fact]
    public void AQuadSplitsAlongItsShorterDiagonal()
    {
        // A flat kite: the 0–2 diagonal spans 6, the 1–3 diagonal spans 2.
        Vector3d a = (0, -3, 0), b = (1, 0, 0), c = (0, 3, 0), d = (-1, 0, 0);
        Assert.Equal(1, PolygonFan.QuadApex(a, b, c, d));   // split b–d
        // Rotating the same cyclic polygon moves the apex with it, so the SPLIT is
        // unchanged — that is the whole point of reading geometry instead of order.
        Assert.Equal(0, PolygonFan.QuadApex(b, c, d, a));   // split b–d
        Assert.Equal(1, PolygonFan.QuadApex(c, d, a, b));   // split d–b
        Assert.Equal(0, PolygonFan.QuadApex(d, a, b, c));   // split d–b
    }

    [Fact]
    public void EqualDiagonals_KeepCornerZero()
    {
        Vector3d a = (0, 0, 0), b = (1, 0, 0), c = (1, 1, 0), d = (0, 1, 0);
        Assert.Equal(0, PolygonFan.QuadApex(a, b, c, d));
    }

    [Fact]
    public void NonQuads_AlwaysFanFromCornerZero()
    {
        Assert.Equal(0, PolygonFan.Apex(new Vector3d[] { (0, 0, 0), (1, 0, 0), (0, 1, 0) }));
        Assert.Equal(0, PolygonFan.Apex(
            new Vector3d[] { (0, 0, 0), (4, 0, 0), (5, 2, 0), (2, 4, 0), (-1, 2, 0) }));
    }

    /// <summary>
    /// The tie guard, stated as the measurement that forced it. Every quad of a UV sphere
    /// is mirror-symmetric about its own meridian, so its two diagonals are reflections of
    /// each other and mathematically EQUAL — but their computed squares differ in the last
    /// ulps, and an exact comparison hands the split to round-off on roughly half of them.
    /// That is the same defect the rule exists to remove (a split decided by something
    /// other than geometry), and it measurably perturbed decimation and remeshing
    /// downstream before the relative guard went in.
    /// </summary>
    [Theory]
    [InlineData(12, 8)]
    [InlineData(40, 26)]
    [InlineData(80, 52)]
    public void SymmetricGridCells_AreNotDecidedByRoundOff(int slices, int stacks)
    {
        var sphere = MeshPrimitives.UvSphere(1.0, slices, stacks);
        var (positions, faces) = sphere.ToIndexed();

        int quads = 0, exactWouldFlip = 0, ruleFlips = 0;
        double worstRatio = 1;
        foreach (var face in faces)
        {
            if (face.Length != 4)
                continue;   // pole fans are triangles
            quads++;
            double across02 = (positions[face[2]] - positions[face[0]]).LengthSquared;
            double across13 = (positions[face[3]] - positions[face[1]]).LengthSquared;
            if (across13 < across02)
                exactWouldFlip++;
            if (PolygonFan.Apex(face, positions) == 1)
                ruleFlips++;
            worstRatio = Math.Max(worstRatio, Math.Sqrt(Math.Max(across02, across13) / Math.Min(across02, across13)));
        }

        output.WriteLine($"UvSphere({slices},{stacks}): {quads} quads, an exact comparison would " +
                         $"flip {exactWouldFlip}, the rule flips {ruleFlips}, worst diagonal ratio {worstRatio:F14}");
        Assert.True(exactWouldFlip > quads / 4,
            $"the fixture stopped being ulp-ambiguous ({exactWouldFlip} of {quads})");
        Assert.True(worstRatio < 1 + 1e-12,
            $"the diagonals are supposed to be mathematically equal here, ratio {worstRatio:F14}");
        Assert.Equal(0, ruleFlips);
    }

    /// <summary>
    /// … and where the diagonals genuinely differ, the rule does fire. A sheared helical
    /// band is the case this whole thing was filed for; a sheared box reproduces it in
    /// four lines.
    /// </summary>
    [Fact]
    public void GenuinelyUnequalDiagonals_DoFlip()
    {
        Vector3d a = (0, 0, 0), b = (10, 0, 0), c = (10, 1, 4), d = (0, 1, 0);
        double across02 = (c - a).LengthSquared, across13 = (d - b).LengthSquared;
        Assert.True(across13 < across02);
        Assert.Equal(1, PolygonFan.QuadApex(a, b, c, d));
    }

    /// <summary>
    /// The invariant the whole exercise exists for: a mirrored solid must measure the same
    /// as its twin. Under the corner-0 fan it did not — mirroring a sheared cell swaps
    /// which diagonal the fan picks, and a left-hand threaded rod carried a systematically
    /// 3x larger volume deficit than its right-hand twin at every density.
    /// </summary>
    [Fact]
    public void MirroringASolid_DoesNotChangeItsMeasuredVolume()
    {
        // A sheared, closed hexahedron: every side face is a non-planar quad.
        var positions = new Vector3d[]
        {
            (0, 0, 0), (4, 0, 0), (4, 3, 0), (0, 3, 0),
            (1, 0.5, 5), (6, 0.2, 5), (5, 4, 5), (0.5, 3.5, 5),
        };
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4],
            [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        var solid = HalfEdgeMesh.Build(positions, faces);
        var mirrored = solid.Transformed(Matrix4d.CreateScale(new Vector3d(-1, 1, 1)));

        Assert.True(solid.Volume() > 0);
        Assert.Equal(solid.Volume(), mirrored.Volume(), 12);
        // And the two triangulations agree face for face, not merely in total.
        Assert.Equal(solid.Triangulated().Volume(), mirrored.Triangulated().Volume(), 12);
    }

    /// <summary>
    /// Every consumer has to fan the same way, or the volume reported is of a solid nobody
    /// draws. The render mesh is the one that is easiest to let drift, since it lives on
    /// the other side of a float conversion.
    /// </summary>
    [Fact]
    public void RenderMeshAndSignedVolume_AgreeOnTheDecomposition()
    {
        var positions = new Vector3d[]
        {
            (0, 0, 0), (4, 0, 0), (4, 3, 0), (0, 3, 0),
            (1, 0.5, 5), (6, 0.2, 5), (5, 4, 5), (0.5, 3.5, 5),
        };
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4],
            [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        var mesh = HalfEdgeMesh.Build(positions, faces);
        var render = RenderMesh.CreateFlat(mesh);

        // Volume of the render mesh's actual triangles, summed the same way.
        double volume = 0;
        for (int t = 0; t < render.TriangleCount; t++)
        {
            var p = new Vector3d[3];
            for (int c = 0; c < 3; c++)
            {
                uint i = render.Indices[t * 3 + c];
                p[c] = new Vector3d(render.Positions[i * 3], render.Positions[i * 3 + 1], render.Positions[i * 3 + 2]);
            }
            volume += p[0].Dot(p[1].Cross(p[2]));
        }
        // Float positions, so agreement is to single precision, not to double.
        Assert.Equal(mesh.Volume(), volume / 6.0, 3);

        // The triangulated mesh is the same decomposition again — this one exactly.
        Assert.Equal(mesh.Volume(), mesh.Triangulated().Volume(), 12);
        // ... and so is the mass-property integrator.
        Assert.Equal(mesh.Volume(), MeshMassProperties.Compute(mesh).Volume, 12);
    }
}
