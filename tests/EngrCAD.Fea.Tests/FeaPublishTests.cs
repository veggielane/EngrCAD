using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Publishing: the results seam to the document model, the sampling step that closes the
/// gap between a solver's vertex set and a display mesh's, and the ParaView export.
/// </summary>
public class FeaPublishTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("publish steel", 210_000, 0.3, 7.85e-9);
    private static readonly Vector3d Size = new(30, 20, 10);

    private static StructuralResults Solve(ElementOrder order, out TetMesh tets)
    {
        tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 6, 4, 2);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -2000));
        return StructuralSolver.Solve(model);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Fields_CarryTheDisplacementAndTheStressOverTheAnalysisNodes(ElementOrder order)
    {
        var results = Solve(order, out _);
        var fields = results.Fields();

        Assert.Equal(2, fields.Count);
        var displacement = fields.Single(f => f.Name == StructuralResults.FieldNames.Displacement);
        var stress = fields.Single(f => f.Name == StructuralResults.FieldNames.VonMises);

        Assert.True(displacement.IsVector);
        Assert.False(stress.IsVector);
        Assert.Equal("mm", displacement.Units);
        Assert.Equal("MPa", stress.Units);
        Assert.Equal(results.Mesh.NodeCount, displacement.Count);
        Assert.Equal(results.Mesh.NodeCount, stress.Count);

        // The field's own range must be the solver's, not a re-derivation of it.
        Assert.Equal(results.MaxVonMises, stress.Range.Max, results.MaxVonMises * 1e-12);
        Assert.Equal(results.MaxDisplacement, displacement.Range.Max, results.MaxDisplacement * 1e-12);

        output.WriteLine($"{order}: {displacement}");
        output.WriteLine($"{order}: {stress}");
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void SamplingOntoTheAnalysisMeshsOwnBoundary_IsExactWithNoSearchAtAll(ElementOrder order)
    {
        // The common case: the mesh handed to the tet mesher IS the display mesh, so every
        // display vertex matches an analysis boundary node BIT FOR BIT and the barycentric
        // fallback never runs. A non-zero sampling distance here would mean the exact-match
        // path had silently stopped working.
        var results = Solve(order, out var tets);
        var surface = tets.BoundaryMesh(out var sourceVertices);

        var fields = results.SampleOnto(surface, out double maxDistance);
        Assert.Equal(0.0, maxDistance);

        var displacement = fields.Single(f => f.Name == StructuralResults.FieldNames.Displacement);
        var stress = fields.Single(f => f.Name == StructuralResults.FieldNames.VonMises);
        Assert.Equal(surface.VertexCount, displacement.Count);

        for (int v = 0; v < surface.VertexCount; v++)
        {
            int node = sourceVertices[v];
            Assert.Equal(results.DisplacementAt(node).X, displacement.VectorAt(v).X);
            Assert.Equal(results.NodalVonMises[node], stress.ValueAt(v));
        }
        output.WriteLine(
            $"{order}: {surface.VertexCount} display vertices, all matched exactly, "
            + $"peak {stress.Range.Max:F4} MPa");
    }

    [Fact]
    public void SamplingOntoADifferentTessellation_InterpolatesOnTheNearestFacet()
    {
        // The general case: a display mesh whose vertices are NOT the analysis mesh's.
        // Every vertex still lands on the analysis boundary (the two describe the same
        // box), so the sampling distance is at round-off, and the values are the facets'
        // own interpolants rather than a nearest-node copy.
        var results = Solve(ElementOrder.Quadratic, out _);
        var display = MeshPrimitives.Box(new Aabb(Vector3d.Zero, Size));

        var fields = results.SampleOnto(display, out double maxDistance);
        var stress = fields.Single(f => f.Name == StructuralResults.FieldNames.VonMises);

        output.WriteLine(
            $"{display.VertexCount} display vertices, max sampling distance {maxDistance:E3} "
            + $"against a {Size.Length:F1} model, peak {stress.Range.Max:F4} MPa");

        Assert.Equal(display.VertexCount, stress.Count);
        Assert.True(maxDistance <= 1e-9 * Size.Length,
            $"sampling distance {maxDistance:E3} — the display mesh is not on the analysis boundary");
        // Interpolated values stay inside the solver's own range: interpolation cannot
        // invent a value outside the nodal data's convex hull.
        Assert.InRange(stress.Range.Min, 0, results.MaxVonMises * 1.000001);
        Assert.InRange(stress.Range.Max, 0, results.MaxVonMises * 1.000001);
    }

    [Fact]
    public void SamplingReportsTheDistanceWhenTheMeshesAreNotTheSameBody()
    {
        // The diagnostic that keeps a wrong pairing from looking like an answer.
        var results = Solve(ElementOrder.Linear, out _);
        var elsewhere = MeshPrimitives.Box(new Aabb(
            new Vector3d(1000, 0, 0), new Vector3d(1000 + Size.X, Size.Y, Size.Z)));

        results.SampleOnto(elsewhere, out double maxDistance);
        output.WriteLine($"sampling a box 1000 units away reports a distance of {maxDistance:F1}");
        Assert.True(maxDistance > 900, $"distance {maxDistance:F1} should expose the mismatch");
    }

    [Theory]
    [InlineData(ElementOrder.Linear, 10)]
    [InlineData(ElementOrder.Quadratic, 24)]
    public void VtuExport_WritesTheVolumeMeshWithTheRightCellType(ElementOrder order, int cellType)
    {
        var results = Solve(order, out _);
        var writer = new StringWriter();
        results.WriteVtu(writer);
        string vtu = writer.ToString();

        Assert.Contains("<VTKFile type=\"UnstructuredGrid\"", vtu);
        Assert.Contains($"NumberOfPoints=\"{results.Mesh.NodeCount}\"", vtu);
        Assert.Contains($"NumberOfCells=\"{results.Mesh.ElementCount}\"", vtu);
        Assert.Contains(StructuralResults.FieldNames.Displacement, vtu);
        Assert.Contains(StructuralResults.FieldNames.VonMises, vtu);

        // Every cell type entry is the element's own code — 10 for VTK_TETRA, 24 for
        // VTK_QUADRATIC_TETRA, whose node order QuadraticTet already follows.
        int start = vtu.IndexOf("Name=\"types\"", StringComparison.Ordinal);
        Assert.True(start > 0, "no cell-type array");
        int from = vtu.IndexOf('>', start) + 1;
        int to = vtu.IndexOf("</DataArray>", from, StringComparison.Ordinal);
        var codes = vtu[from..to].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(results.Mesh.ElementCount, codes.Length);
        Assert.All(codes, c => Assert.Equal(cellType.ToString(), c));

        output.WriteLine($"{order}: {vtu.Length:N0} characters, cell type {cellType}");
    }

    [Fact]
    public void ResultsReachAPartAndResolveForTheViewer()
    {
        // The end-to-end seam: solve on a tet mesh, sample onto the PART's display mesh,
        // attach, and resolve the display the way every render path does. The contract
        // Part.AddResult enforces is field.Count == GetMesh().VertexCount, which is
        // exactly what the sampling step exists to satisfy.
        var part = new Part("bracket", Shape.Box(Size.X, Size.Y, Size.Z));
        var display = part.GetMesh();

        var bounds = Aabb.Empty;
        for (int v = 0; v < display.VertexCount; v++)
            bounds = bounds.Union(display.GetPosition(v));

        var tets = StructuredTetMesh.Box(bounds.Min, bounds.Size, 6, 4, 2);
        var mesh = AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -2000));
        var results = StructuralSolver.Solve(model);

        var sampled = results.SampleOnto(display, out double distance);
        foreach (var field in sampled)
            part.AddResult(field);
        Assert.True(distance <= 1e-9 * bounds.Size.Length, $"sampling distance {distance:E3}");

        part.FieldDisplay = new FieldDisplay
        {
            Field = StructuralResults.FieldNames.VonMises,
            Deform = StructuralResults.FieldNames.Displacement,
            DeformScale = 50,
        };

        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? error), error);
        Assert.Equal(StructuralResults.FieldNames.VonMises, resolved.Field.Name);
        Assert.NotNull(resolved.Deform);
        Assert.Equal(50, resolved.DeformScale);
        Assert.Equal(display.VertexCount, resolved.Field.Count);
        Assert.False(resolved.Range.IsEmpty);

        output.WriteLine(
            $"part carries {part.Results.Count} results over {display.VertexCount} display vertices; "
            + $"legend '{resolved.Label}' spanning {resolved.Range}");

        // Re-solving replaces rather than accumulates — the same-name rule.
        foreach (var field in results.SampleOnto(display))
            part.AddResult(field);
        Assert.Equal(2, part.Results.Count);
    }
}
