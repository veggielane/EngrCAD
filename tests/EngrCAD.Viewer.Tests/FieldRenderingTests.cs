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
        // No deformation asked for: no displacement buffer and no ghost.
        Assert.False(data.Deformed);
        Assert.Null(data.Deformation);
        Assert.Equal(0, data.DeformScale);
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
    public void TryBuild_CarriesTheDisplacementPerUnitScale()
    {
        // The buffer holds the displacement per UNIT scale: the exaggeration is the
        // uniform, which is exactly what makes animating it free.
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 2,
        });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));
        Assert.True(data.Deformed);
        Assert.Equal(2, data.DeformScale);
        var deformation = Assert.IsType<float[]>(data.Deformation);
        Assert.Equal(render.VertexCount * FieldRendering.DeformationStride, deformation.Length);

        var displacement = part.Result("u")!;
        for (int v = 0; v < render.VertexCount; v++)
        {
            var d = displacement.VectorAt(render.SourceVertices[v]);
            int at = v * FieldRendering.DeformationStride;
            Assert.Equal((float)d.X, deformation[at]);
            Assert.Equal((float)d.Y, deformation[at + 1]);
            Assert.Equal((float)d.Z, deformation[at + 2]);
        }
        Assert.True(data.ShowGhost);   // ShowUndeformed defaults on
    }

    [Fact]
    public void DeformationAttributes_ReproduceTheDisplacedFacetNormalAtEveryScale()
    {
        // THE identity the design rests on: a triangle whose vertices move linearly in s
        // has a facet normal that is exactly QUADRATIC in s, so three coefficient vectors
        // reproduce what the CPU path recomputed — at every scale, from data sent once.
        // Checked against Deform, the readable statement of the same thing.
        var (part, render, _) = Plate();
        var displacement = part.Result("u")!;
        var attributes = FieldRendering.DeformationAttributes(render, displacement);

        foreach (double scale in new[] { 0.0, 0.25, 1.0, 3.5, -2.0 })
        {
            var reference = FieldRendering.Deform(render, displacement, scale);
            for (int v = 0; v < render.VertexCount; v++)
            {
                int at = v * FieldRendering.DeformationStride;
                // The shader evaluates n0 + s*n1 + s*s*n2 and normalizes in the fragment
                // stage, so only the DIRECTION is claimed.
                var n = Coefficient(attributes, at + 3)
                    + Coefficient(attributes, at + 6) * scale
                    + Coefficient(attributes, at + 9) * (scale * scale);
                var expected = new Vector3d(
                    reference.Normals[v * 3], reference.Normals[v * 3 + 1], reference.Normals[v * 3 + 2]);
                Assert.True(n.Length > 0, $"vertex {v} at scale {scale} has no normal");
                Assert.Equal(1.0, (n / n.Length).Dot(expected), 5);

                // And the position the shader computes matches the CPU one.
                var offset = Coefficient(attributes, at) * scale;
                Assert.Equal(render.Positions[v * 3] + (float)offset.X, reference.Positions[v * 3], 4);
                Assert.Equal(render.Positions[v * 3 + 1] + (float)offset.Y, reference.Positions[v * 3 + 1], 4);
                Assert.Equal(render.Positions[v * 3 + 2] + (float)offset.Z, reference.Positions[v * 3 + 2], 4);
            }
        }

        static Vector3d Coefficient(float[] attributes, int at) =>
            new(attributes[at], attributes[at + 1], attributes[at + 2]);
    }

    [Fact]
    public void DeformationAttributes_TurnTheNormalsFarEnoughToMatterAtRealisticExaggerations()
    {
        // WHY the shader carries three normal coefficients rather than reusing aNormal.
        // The docs' cantilever law at its own 40x, on a strip fine enough to resolve the
        // bend. The fixture is a hand-built 30-span sheet on purpose: a bare
        // Shape.Box is ONE quad per face, so its facet normal averages the whole span and
        // reports 6.7 degrees where the resolved surface turns 9.9 — the
        // fixture-understates-the-effect trap. Measured rather than argued, because
        // "carrying the source normals over costs little" is exactly the kind of claim
        // that is true until it is not.
        const double length = 120, tip = 0.35;
        const int spans = 30;
        var positions = new List<Vector3d>();
        for (int i = 0; i <= spans; i++)
        {
            positions.Add((i * length / spans, 0, 0));
            positions.Add((i * length / spans, 24, 0));
        }
        var quads = new List<int[]>();
        for (int i = 0; i < spans; i++)
            quads.Add([i * 2, i * 2 + 2, i * 2 + 3, i * 2 + 1]);
        var mesh = HalfEdgeMesh.Build(positions, quads);
        var render = RenderMesh.CreateFlat(mesh);
        var displacement = MeshField.SampleVector(mesh, "u", "mm",
            p => new Vector3d(0, 0, -tip * (Sq(p.X) * (3 * length - p.X))
                / (2 * length * length * length)));

        var deformed = FieldRendering.Deform(render, displacement, 40);
        double worst = 1;
        for (int v = 0; v < render.VertexCount; v++)
        {
            var before = new Vector3d(
                render.Normals[v * 3], render.Normals[v * 3 + 1], render.Normals[v * 3 + 2]);
            var after = new Vector3d(
                deformed.Normals[v * 3], deformed.Normals[v * 3 + 1], deformed.Normals[v * 3 + 2]);
            worst = Math.Min(worst, before.Dot(after));
        }
        // Against the analytic slope rather than a threshold: a tip-loaded cantilever's
        // free end has slope 3*tip/(2L), so at 40x the outermost facet turns by
        // atan(40*3*tip/(2L)) = 9.93 degrees. Reproduced to a tenth of a degree, and 10
        // degrees of normal error is a ~12% shading error under a 45-degree key light —
        // small enough that guessing would have got it wrong in both directions, which is
        // why it is measured.
        double turned = Math.Acos(worst) * 180 / Math.PI;
        double analytic = Math.Atan(40 * 3 * tip / (2 * length)) * 180 / Math.PI;
        Assert.Equal(analytic, turned, 1);
        Assert.True(turned > 5, $"deformed normals turned by only {turned:F1} degrees");

        static double Sq(double x) => x * x;
    }

    [Fact]
    public void DeformationAttributes_VanishWhereTheDisplacedTriangleCollapses()
    {
        // An all-zero normal expression is the shader's signal to fall back to aNormal,
        // the same exact-zero fallback Deform makes. Here every vertex moves to the
        // origin, so at scale 1 every facet is a point.
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var render = RenderMesh.CreateFlat(box);
        var collapse = MeshField.SampleVector(box, "u", "", p => -p);
        var attributes = FieldRendering.DeformationAttributes(render, collapse);

        for (int v = 0; v < render.VertexCount; v++)
        {
            int at = v * FieldRendering.DeformationStride;
            var n = new Vector3d(attributes[at + 3], attributes[at + 4], attributes[at + 5])
                + new Vector3d(attributes[at + 6], attributes[at + 7], attributes[at + 8])
                + new Vector3d(attributes[at + 9], attributes[at + 10], attributes[at + 11]);
            Assert.Equal(0, n.Length, 6);
        }
        Assert.All(attributes, a => Assert.False(float.IsNaN(a)));
    }

    [Fact]
    public void DeformUniform_FormsTheProductInDoubleAndNarrowsOnce()
    {
        // The byte-for-byte equality between an animated frame and a static render of the
        // same configuration rests on this: a part at s animated to factor f must send the
        // identical float a part displayed at s*f sends.
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 0.1,
        });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var animated, out _));

        var (stillPart, stillRender, stillCount) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 0.1 * 0.3,
        });
        Assert.True(FieldRendering.TryBuild(stillPart, stillRender, stillCount, out var still, out _));

        Assert.Equal(FieldRendering.DeformUniform(still, 1), FieldRendering.DeformUniform(animated, 0.3));
    }

    [Fact]
    public void PickShape_DisplacesAtThePartsOwnScaleAndOtherwiseReturnsTheSourceMesh()
    {
        // A pick BVH is a spatial index, not a uniform, so it is built once at the part's
        // own exaggeration — the animation's factor-1 configuration. Documented, tested.
        var (part, render, count) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 2,
        });
        Assert.True(FieldRendering.TryBuild(part, render, count, out var data, out _));
        var picked = FieldRendering.PickShape(render, data);
        Assert.NotSame(render, picked);
        Assert.Equal(FieldRendering.Deform(render, part.Result("u")!, 2).Positions, picked.Positions);

        var (plain, plainRender, plainCount) = Plate(
            p => p.FieldDisplay = new FieldDisplay { Field = "stress" });
        Assert.True(FieldRendering.TryBuild(plain, plainRender, plainCount, out var flat, out _));
        Assert.Same(plainRender, FieldRendering.PickShape(plainRender, flat));
    }

    [Fact]
    public void AtFactor_ScalesTheLegendsExaggerationAndLeavesFactorOneAlone()
    {
        var (part, _, _) = Plate(p => p.FieldDisplay = new FieldDisplay
        {
            Field = "stress",
            Deform = "u",
            DeformScale = 40,
        });
        Assert.True(part.TryResolveFieldDisplay(out var display, out _));
        Assert.Equal(display, FieldRendering.AtFactor(display, 1));
        Assert.Equal(20, FieldRendering.AtFactor(display, 0.5)!.Value.DeformScale);
        Assert.Null(FieldRendering.AtFactor(null, 0.5));
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
        Assert.False(data.Deformed);
        Assert.Null(data.Deformation);
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
        Assert.True(data.Deformed);
        Assert.False(data.ShowGhost);
    }

    [Fact]
    public void SourceColors_LogScale_MapsTheDecadeMidpointToTheMapsMiddle()
    {
        // 10^3 is the LOG midpoint of [10, 10^5]; linearly it sits at t = 0.0099, so
        // the two mappings measurably disagree there while agreeing at both ends.
        var field = MeshField.Scalar("life", "cycles", [10, 1e3, 1e5]);
        var range = new FieldRange(10, 1e5);
        var log = FieldRendering.SourceColors(field, range, FieldColorMap.Viridis, logScale: true);

        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0.0), log[0]);
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0.5), log[1]);
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 1.0), log[2]);

        var linear = FieldRendering.SourceColors(field, range, FieldColorMap.Viridis);
        Assert.Equal(linear[0], log[0]);
        Assert.Equal(linear[2], log[2]);
        Assert.NotEqual(linear[1], log[1]);
    }

    [Fact]
    public void SourceColors_LogScale_ANonPositiveValueTakesTheNoValueColour()
    {
        // A log scale has no position for zero or a negative value: it maps through
        // NaN to ColorMaps.NoValueColor — the same "no value here" convention NaN
        // already takes on a linear display — and NOT to the map's bottom stop, which
        // would paint it the colour of the smallest finite value.
        var field = MeshField.Scalar("life", "cycles", [0.0, -5, double.NaN, 10]);
        var range = new FieldRange(10, 1e5);
        var colors = FieldRendering.SourceColors(field, range, FieldColorMap.Viridis, logScale: true);

        Assert.Equal(ColorMaps.NoValueColor, colors[0]);
        Assert.Equal(ColorMaps.NoValueColor, colors[1]);
        Assert.Equal(ColorMaps.NoValueColor, colors[2]);
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0.0), colors[3]);
        Assert.NotEqual(ColorMaps.NoValueColor, colors[3]);
    }

    [Fact]
    public void SourceColors_NaN_TakesTheNoValueColourOnALinearDisplay()
    {
        // NaN is "no value", not "small": an infinite-life node must not paint the
        // colour of the shortest finite life. An exact zero is genuinely the range
        // minimum and keeps the bottom stop — the two are different statements.
        var field = MeshField.Scalar("life", "cycles", [double.NaN, 0.0, 100.0]);
        var range = new FieldRange(0, 100);
        var colors = FieldRendering.SourceColors(field, range, FieldColorMap.Viridis);

        Assert.Equal(ColorMaps.NoValueColor, colors[0]);
        Assert.Equal(ColorMaps.Sample(FieldColorMap.Viridis, 0.0), colors[1]);
        Assert.NotEqual(colors[0], colors[1]);
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
        Assert.Throws<ArgumentException>(
            () => FieldRendering.DeformationAttributes(render, MeshField.Scalar("s", "", [1])));
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
