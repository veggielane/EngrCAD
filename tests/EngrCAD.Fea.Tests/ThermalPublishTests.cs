using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Thermal publishing: the same <see cref="MeshField"/> and <c>.vtu</c> seam a structural
/// solve leaves through, so the viewer's colour map and the exporters need no new wiring.
/// </summary>
public class ThermalPublishTests(ITestOutputHelper output)
{
    private static readonly Material Metal =
        new("publish metal", 200_000, 0.3, 8e-9, thermalConductivity: 40.0, specificHeat: 5e8);

    private static readonly Vector3d Size = new(30, 20, 10);

    private static ThermalResults Solve(ElementOrder order, out TetMesh tets)
    {
        tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 6, 4, 2);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        var model = new ThermalModel(mesh, Metal)
            .Temperature(StructuredTetMesh.XMin, 120)
            .Convection(StructuredTetMesh.XMax, 0.05, 20);
        return ThermalSolver.Solve(model);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Fields_CarryTheTemperatureAndTheFluxOverTheAnalysisNodes(ElementOrder order)
    {
        var results = Solve(order, out _);
        var fields = results.Fields();

        Assert.Equal(2, fields.Count);
        var temperature = fields.Single(f => f.Name == ThermalResults.FieldNames.Temperature);
        var flux = fields.Single(f => f.Name == ThermalResults.FieldNames.HeatFlux);

        Assert.False(temperature.IsVector);
        Assert.True(flux.IsVector);
        Assert.Equal("C", temperature.Units);
        Assert.Equal("mW/mm^2", flux.Units);
        Assert.Equal(results.Mesh.NodeCount, temperature.Count);
        Assert.Equal(results.Mesh.NodeCount, flux.Count);

        // The field's own range must be the solver's, not a re-derivation of it.
        Assert.Equal(results.MaxTemperature, temperature.Range.Max, Math.Abs(results.MaxTemperature) * 1e-12);
        Assert.Equal(results.MinTemperature, temperature.Range.Min, Math.Abs(results.MinTemperature) * 1e-12);
        Assert.Equal(results.MaxFluxMagnitude, flux.Range.Max, results.MaxFluxMagnitude * 1e-12);

        // "Colour by heat flux" needs no extra call: ScalarAt of a vector field is its
        // magnitude, which is what the document model's FieldDisplay reads.
        for (int v = 0; v < flux.Count; v++)
            Assert.Equal(results.NodalFlux[v].Length, flux.ScalarAt(v), 1e-9);

        output.WriteLine($"{order}: {temperature}");
        output.WriteLine($"{order}: {flux}");
    }

    /// <summary>
    /// The flux points the way heat actually goes: from the hot face towards the cool one,
    /// i.e. along +x here. A sign error in <c>q = -k·grad T</c> is invisible in a
    /// temperature plot and obvious in this one.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void HeatFlux_PointsFromHotToCold(ElementOrder order)
    {
        var results = Solve(order, out _);
        double worstOffAxis = 0;
        double smallestAxial = double.MaxValue;
        for (int e = 0; e < results.Mesh.ElementCount; e++)
        {
            var q = results.ElementFlux(e);
            smallestAxial = Math.Min(smallestAxial, q.X);
            worstOffAxis = Math.Max(worstOffAxis, Math.Sqrt(q.Y * q.Y + q.Z * q.Z));
        }

        output.WriteLine(
            $"{order}: smallest q.x = {smallestAxial:G6} (all must be positive, heat runs "
            + $"from the 120 C face to the film); worst transverse |q| = {worstOffAxis:E3}");
        Assert.True(smallestAxial > 0, $"an element's flux runs the wrong way: {smallestAxial:G6}");
        // The problem is one-dimensional, so there is no transverse flux at all.
        Assert.True(worstOffAxis / smallestAxial < 1e-12, $"{worstOffAxis:E3}");
    }

    /// <summary>
    /// Sampling onto the analysis mesh's own source surface is exact and needs no search:
    /// every display vertex is a boundary node, matched by exact bits.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void SamplingOntoTheSourceSurface_IsExact(ElementOrder order)
    {
        var results = Solve(order, out var tets);
        var surface = tets.BoundaryMesh(out _);

        var fields = results.SampleOnto(surface, out double maxDistance);
        var temperature = fields.Single(f => f.Name == ThermalResults.FieldNames.Temperature);

        output.WriteLine(
            $"{order}: {surface.VertexCount:N0} display vertices, worst sampling distance "
            + $"{maxDistance:E3}");
        Assert.Equal(0, maxDistance);
        Assert.Equal(surface.VertexCount, temperature.Count);

        // Every value is a boundary node's own, not an interpolation of one.
        var byPosition = new Dictionary<Vector3d, double>();
        for (int f = 0; f < results.Mesh.FacetCount; f++)
        {
            foreach (int node in results.Mesh.Facet(f))
                byPosition[results.Mesh.Position(node)] = results.TemperatureAt(node);
        }
        for (int v = 0; v < surface.VertexCount; v++)
            Assert.Equal(byPosition[surface.GetPosition(v)], temperature.ValueAt(v), 12);
    }

    /// <summary>The ParaView export carries both arrays and the right cell type.</summary>
    [Theory]
    [InlineData(ElementOrder.Linear, "10")]
    [InlineData(ElementOrder.Quadratic, "24")]
    public void WriteVtu_CarriesBothArraysAndTheRightCellType(ElementOrder order, string cellType)
    {
        var results = Solve(order, out _);
        var writer = new StringWriter();
        results.WriteVtu(writer);
        string xml = writer.ToString();

        Assert.Contains("<VTKFile type=\"UnstructuredGrid\"", xml);
        Assert.Contains($"\"{ThermalResults.FieldNames.Temperature}", xml);
        Assert.Contains($"\"{ThermalResults.FieldNames.HeatFlux}", xml);
        Assert.Contains("NumberOfComponents=\"3\"", xml);
        // VTK_TETRA is 10, VTK_QUADRATIC_TETRA is 24.
        Assert.Contains(cellType, xml);

        output.WriteLine(
            $"{order}: {xml.Length:N0} characters, {results.Mesh.ElementCount:N0} cells of "
            + $"type {cellType}");
    }

    /// <summary>
    /// A transient state publishes exactly as a steady one does, which is what makes a
    /// time slider a matter of choosing a state rather than a second API.
    /// </summary>
    [Fact]
    public void EveryTransientStatePublishesLikeASteadyOne()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4, 3, 2);
        var mesh = AnalysisMesh.Of(tets);
        var model = new ThermalModel(mesh, Metal).Temperature(StructuredTetMesh.XMin, 120);

        var run = ThermalSolver.SolveTransient(
            model,
            new ThermalTransientOptions(0.05, 20) { InitialTemperature = 20, StoreEvery = 5 });

        output.WriteLine($"{run.Count} stored states at t = {string.Join(", ", run.Times)}");
        foreach (var state in run.States)
        {
            var fields = state.Fields();
            Assert.Equal(2, fields.Count);
            Assert.Equal(mesh.NodeCount, fields[0].Count);
            output.WriteLine(
                $"  t = {state.Time,5:F2}: {state.MinTemperature,8:F3} to "
                + $"{state.MaxTemperature,8:F3} C, peak |q| {state.MaxFluxMagnitude,10:F3}");
        }

        // The states are genuinely different fields and not the same one published several
        // times — measured on the MEAN, which rises monotonically because heat only enters.
        // Not on the minimum: the coldest node briefly dips BELOW the initial temperature
        // (20.000 -> 15.884 -> 16.293 here), which is the consistent capacity matrix's
        // undershoot at a step change and not a sign that time is running backwards.
        double previousMean = double.MinValue;
        foreach (var state in run.States)
        {
            double mean = state.Temperature.Average();
            Assert.True(mean > previousMean, $"mean {mean:F4} did not rise at t = {state.Time}");
            previousMean = mean;
        }
    }
}
