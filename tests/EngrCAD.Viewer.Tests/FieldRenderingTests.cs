using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

public class FieldRenderingTests
{
    private static (Part Part, RenderMesh Render, int VertexCount) Plate(
        Action<Part>? configure = null)
    {
        var part = new Part("plate", Shape.Box(20, 10, 2));
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "stress", "MPa", p => p.Z));
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm", p => new Vector3d(0, 0, p.X)));
        configure?.Invoke(part);
        return (part, RenderMesh.CreateFlat(mesh), mesh.VertexCount);
    }

    [Fact]
    public void TryBuild_APartWithNoDisplay_IsNotAFailure()
    {
        var (part, render, count) = Plate();
        Assert.False(FieldRendering.TryBuild(part, render, count, out _, out string? error));
        Assert.Null(error);   // "nothing to show" is not "it went wrong"
    }

    [Fact]
    public void TryBuild_ColorsEveryRenderVertexFromItsSourceVertex()
    {
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));

        Assert.Equal(render.VertexCount * 3, data.Colors.Length);
        var field = part.Result("stress")!;
        for (int v = 0; v < render.VertexCount; v++)
        {
            var expected = ColorMaps.Sample(
                FieldColorMap.Viridis, field.Range, field.ScalarAt(render.SourceVertices[v]));
            Assert.Equal(expected.R, data.Colors[v * 3]);
            Assert.Equal(expected.G, data.Colors[v * 3 + 1]);
            Assert.Equal(expected.B, data.Colors[v * 3 + 2]);
        }
        // No deformation asked for: the geometry is untouched and there is no ghost.
        Assert.Null(data.Deformed);
        Assert.False(data.ShowGhost);
    }

    [Fact]
    public void TryBuild_DuplicatesOfOneSourceVertexGetTheSameColor()
    {
        // The flat render mesh repeats a position once per incident triangle; a field is
        // per SOURCE vertex, so the copies must agree or a box corner would show a seam.
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));

        var first = new Dictionary<int, int>();
        for (int v = 0; v < render.VertexCount; v++)
        {
            int source = render.SourceVertices[v];
            if (!first.TryGetValue(source, out int seen))
            {
                first[source] = v;
                continue;
            }
            for (int c = 0; c < 3; c++)
                Assert.Equal(data.Colors[seen * 3 + c], data.Colors[v * 3 + c]);
        }
    }

    [Fact]
    public void TryBuild_AnExplicitRangeChangesTheColors()
    {
        var (a, renderA, countA) = Plate(p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        var (b, renderB, countB) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Range = new FieldRange(-100, 100),   // the field spans 0..2, so everything sits mid-map
        });
        Assert.True(FieldRendering.TryBuild(a, renderA, countA, out var own, out _));
        Assert.True(FieldRendering.TryBuild(b, renderB, countB, out var wide, out _));
        Assert.NotEqual(own.Colors, wide.Colors);
    }

    [Fact]
    public void TryBuild_DeformsAndRecomputesNormals()
    {
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 2,
        });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));
        var deformed = Assert.IsType<RenderMesh>(data.Deformed);

        Assert.Equal(render.VertexCount, deformed.VertexCount);
        Assert.Equal(render.Indices, deformed.Indices);
        Assert.Equal(render.SourceVertices, deformed.SourceVertices);
        var displacement = part.Result("u")!;
        for (int v = 0; v < render.VertexCount; v++)
        {
            var d = displacement.VectorAt(render.SourceVertices[v]) * 2;
            Assert.Equal(render.Positions[v * 3] + (float)d.X, deformed.Positions[v * 3], 4);
            Assert.Equal(render.Positions[v * 3 + 2] + (float)d.Z, deformed.Positions[v * 3 + 2], 4);
        }
        // The displacement shears the plate, so at least some facet normals must have
        // turned — carrying the source normals over would make the deformed shape look
        // exactly like the original, which is the whole point of the plot.
        Assert.NotEqual(render.Normals, deformed.Normals);
        // Every normal is still a unit vector.
        for (int v = 0; v < deformed.VertexCount; v++)
        {
            double length = new Vector3d(
                deformed.Normals[v * 3], deformed.Normals[v * 3 + 1], deformed.Normals[v * 3 + 2]).Length;
            Assert.Equal(1, length, 4);
        }
        Assert.True(data.ShowGhost);   // ShowUndeformed defaults on
    }

    [Fact]
    public void TryBuild_AZeroDeformScaleLeavesTheGeometryAlone()
    {
        // Exact-zero semantic test: "no exaggeration" means "do not build a second mesh",
        // not "build one that happens to coincide".
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 0,
        });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));
        Assert.Null(data.Deformed);
        Assert.False(data.ShowGhost);
    }

    [Fact]
    public void TryBuild_ShowUndeformedFalse_DrawsNoGhost()
    {
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            ShowUndeformed = false,
        });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));
        Assert.NotNull(data.Deformed);
        Assert.False(data.ShowGhost);
    }

    [Fact]
    public void TryBuild_NamesAResultOfTheWrongLength()
    {
        var part = new Part("plate", Shape.Box(20, 10, 2));
        part.AddResult(MeshField.Scalar("stress", "MPa", [1, 2, 3]));
        part.FieldDisplay = new FieldDisplay { Field = "stress" };
        var render = RenderMesh.CreateFlat(part.GetMesh());

        Assert.False(FieldRendering.TryBuild(
            part, render, part.GetMesh().VertexCount, out _, out string? error));
        Assert.Contains("3 vertices", error);
        Assert.Contains("vertex order", error);
    }

    [Fact]
    public void Deform_RefusesAScalarField()
    {
        var (_, render, _) = Plate();
        Assert.Throws<ArgumentException>(
            () => FieldRendering.Deform(render, MeshField.Scalar("s", "", [1]), 1));
    }

    [Fact]
    public void Deform_KeepsTheSourceNormalWhereATriangleCollapses()
    {
        // A displacement that folds a triangle to zero area has no normal to compute;
        // the exact-zero fallback keeps the source facet's rather than emitting NaN.
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var render = RenderMesh.CreateFlat(box);
        var collapse = MeshField.SampleVector(box, "u", "", p => -p);   // every vertex to the origin
        var deformed = FieldRendering.Deform(render, collapse, 1);

        Assert.All(deformed.Normals, n => Assert.False(float.IsNaN(n)));
        Assert.Equal(render.Normals, deformed.Normals);
    }
}
