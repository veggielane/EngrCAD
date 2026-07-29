using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The mesher through the FULL modelling pipeline: a design built with the <c>Shape</c> API,
/// lowered through the B-Rep engine to a mesh, then tetrahedralized. This is the suite that
/// would catch the mesher being correct only on hand-built primitives.
///
/// <para>Note the oracle discipline throughout: the mesher's own claim is that it fills the
/// INPUT SURFACE, so every volume assertion is against that surface's measured volume, and
/// analytic values appear only with an explicit discretization allowance. Topology is
/// asserted the same way — the boundary's Euler characteristic must match the input's, which
/// states "topology is preserved" without hard-coding what the modelling layer produced.</para>
/// </summary>
public class ShapePipelineTests
{
    /// <summary>A plate with a through-hole bolt pattern: genus 4, built by explicit
    /// subtraction so the test states its own geometry rather than inheriting a feature's
    /// placement conventions.</summary>
    private static Shape DrilledPlate(double sizeX, double sizeY, double thickness, double boreRadius)
    {
        var plate = Shape.Box(sizeX, sizeY, thickness);
        foreach (var (x, y) in new[] { (-12.0, -9.0), (12.0, -9.0), (-12.0, 9.0), (12.0, 9.0) })
            plate -= Shape.Cylinder(boreRadius, thickness * 3).Translate(new Vector3d(x, y, 0));
        return plate;
    }

    [Fact]
    public void DrilledPlate_FillsItsSurfaceAndPreservesGenusFour()
    {
        var surface = DrilledPlate(40, 30, 6, 2.75)
            .ToMesh(new MeshQuality { SegmentsPerCircle = 24 });
        Assert.True(surface.IsClosed);

        // Genus 4 => chi = 2 - 2g = -6. Assert the INPUT is what the test thinks it is
        // before asking anything of the mesher.
        int inputChi = surface.VertexCount - surface.EdgeCount + surface.FaceCount;
        Assert.Equal(-6, inputChi);

        var tets = TetMesher.Mesh(surface, null, out var report);

        Assert.True(report.VolumeResidual < 1e-10, $"volume residual {report.VolumeResidual:E3}");
        Assert.Equal(surface.Volume(), tets.Volume, Math.Abs(surface.Volume()) * 1e-9);

        var skin = tets.BoundaryMesh(out _);
        Assert.True(skin.IsClosed);
        Assert.Equal(inputChi, skin.VertexCount - skin.EdgeCount + skin.FaceCount);

        // Against the analytic solid, with the 24-gon bores' inscribed deficit allowed for.
        double analytic = 40 * 30 * 6 - 4 * Math.PI * 2.75 * 2.75 * 6;
        Assert.Equal(analytic, tets.Volume, analytic * 0.01);

        var quality = TetQuality.Analyze(tets);
        Assert.Equal(tets.TetCount, quality.TetCount);
        Assert.True(quality.MinDihedralDegrees > 0);
        Assert.Equal(tets.Volume, quality.TotalVolume, Math.Abs(tets.Volume) * 1e-12);
    }

    [Fact]
    public void FacetTags_PartitionTheBoundaryAndAreGeometricallyTrue()
    {
        // Tag every input triangle by which face of the plate it belongs to, then check the
        // tags come back partitioning the tet mesh's boundary - the property
        // TetFacet.SourceTriangle exists to carry, and the seam a solver's boundary
        // conditions would attach to.
        var surface = DrilledPlate(40, 30, 6, 2.75)
            .ToMesh(new MeshQuality { SegmentsPerCircle = 16 }).Triangulated();
        var (positions, faces) = surface.ToIndexed();

        const int Top = 0, Bottom = 1, Other = 2;
        var tags = new int[faces.Count];
        for (int f = 0; f < faces.Count; f++)
        {
            var centroid = (positions[faces[f][0]] + positions[faces[f][1]] + positions[faces[f][2]]) / 3.0;
            tags[f] = centroid.Z > 2.999 ? Top : centroid.Z < -2.999 ? Bottom : Other;
        }
        Assert.All(new[] { Top, Bottom, Other }, tag => Assert.Contains(tag, tags));

        var tets = TetMesher.Mesh(surface, new TetMeshOptions { FacetTags = tags }, out var report);

        Assert.Equal(3, tets.BoundaryFacets.Select(f => f.SourceTriangle).Distinct().Count());

        // The tag is a claim about geometry, so it is checked against geometry: every facet
        // tagged Top must actually lie in the z = +3 plane, and so on.
        double total = 0, top = 0, bottom = 0;
        foreach (var facet in tets.BoundaryFacets)
        {
            var a = tets.Position(facet.V0);
            var b = tets.Position(facet.V1);
            var c = tets.Position(facet.V2);
            double area = 0.5 * (b - a).Cross(c - a).Length;
            total += area;

            switch (facet.SourceTriangle)
            {
                case Top:
                    Assert.All([a.Z, b.Z, c.Z], z => Assert.Equal(3.0, z, 9));
                    top += area;
                    break;
                case Bottom:
                    Assert.All([a.Z, b.Z, c.Z], z => Assert.Equal(-3.0, z, 9));
                    bottom += area;
                    break;
                default:
                    Assert.All([a.Z, b.Z, c.Z], z => Assert.InRange(z, -3.0000001, 3.0000001));
                    break;
            }
        }

        // Top and bottom are the plate's face area less four bores, each way.
        double faceArea = 40 * 30 - 4 * 8 * 2.75 * 2.75 * Math.Sin(2 * Math.PI / 16);
        Assert.Equal(faceArea, top, faceArea * 1e-9);
        Assert.Equal(faceArea, bottom, faceArea * 1e-9);
        Assert.True(total > top + bottom, "the sides and bores must contribute area too");
        Assert.True(report.VolumeResidual < 1e-10);
    }

    [Fact]
    public void SdfSizingField_GradesTowardsAFeature()
    {
        // An Sdf composes naturally as a sizing field, which is one reason the hook is a
        // Func<Vector3d, double> rather than a fixed table: the field below asks for fine
        // elements at the bore wall and coarser ones away from it.
        var surface = Shape.Box(16, 10, 4)
            .Subtract(Shape.Cylinder(2.5, 20))
            .ToMesh(new MeshQuality { SegmentsPerCircle = 12 });

        var bore = Sdf.Cylinder(2.5, 20);
        var tets = TetMesher.Mesh(surface, new TetMeshOptions
        {
            RefineQuality = true,
            RadiusEdgeRatio = 2.0,
            SizingField = p => 1.8 + 1.4 * Math.Max(0, bore.Evaluate(p)),
        }, out var report);

        Assert.True(report.VolumeResidual < 1e-9, $"residual {report.VolumeResidual:E3}");
        Assert.Equal(surface.Volume(), tets.Volume, Math.Abs(surface.Volume()) * 1e-9);

        double nearSum = 0, farSum = 0;
        int nearCount = 0, farCount = 0;
        for (int t = 0; t < tets.TetCount; t++)
        {
            var e = tets.GetTet(t);
            var centroid = (tets.Position(e.A) + tets.Position(e.B)
                          + tets.Position(e.C) + tets.Position(e.D)) * 0.25;
            double size = Math.Cbrt(tets.TetVolume(t));
            double distance = bore.Evaluate(centroid);
            if (distance < 0.8) { nearSum += size; nearCount++; }
            else if (distance > 2.5) { farSum += size; farCount++; }
        }

        Assert.True(nearCount > 5 && farCount > 5, $"near {nearCount}, far {farCount}");
        Assert.True(nearSum / nearCount < farSum / farCount,
            $"mean element size near the bore {nearSum / nearCount:F3} was not below the far mean " +
            $"{farSum / farCount:F3}");
    }

    [Fact]
    public void QuadraticElements_FromAShapePipelineMesh_AreContinuousAcrossElements()
    {
        var surface = Shape.Box(20, 10, 5)
            .Subtract(Shape.Cylinder(2.0, 20).Translate(new Vector3d(-5, 0, 0)))
            .Subtract(Shape.Cylinder(2.0, 20).Translate(new Vector3d(5, 0, 0)))
            .ToMesh(new MeshQuality { SegmentsPerCircle = 16 });

        var tets = TetMesher.Mesh(surface);
        var quadratic = QuadraticTetMesh.From(tets);

        Assert.Equal(tets.TetCount, quadratic.TetCount);
        Assert.Equal(tets.Volume, quadratic.Volume, Math.Abs(tets.Volume) * 1e-13);

        var edges = new HashSet<(int, int)>();
        for (int t = 0; t < tets.TetCount; t++)
        {
            var e = tets.GetTet(t);
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                {
                    int a = e[i], b = e[j];
                    edges.Add(a < b ? (a, b) : (b, a));
                }
        }
        Assert.Equal(tets.VertexCount + edges.Count, quadratic.NodeCount);
    }
}
