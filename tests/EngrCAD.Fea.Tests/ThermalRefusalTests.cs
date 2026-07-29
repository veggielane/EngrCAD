using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What a conduction model refuses, and whether the message says enough to act on.
///
/// <para>Every test here asserts the CONTENT of the refusal, not merely that one happened.
/// A solver that throws is only better than one that returns nonsense if the message names
/// what went wrong and where — which is the whole reason the structural solver builds its
/// rigid-body description rather than reporting a rank.</para>
/// </summary>
public class ThermalRefusalTests(ITestOutputHelper output)
{
    private static readonly Material Metal =
        new("refusal metal", 200_000, 0.3, 8e-9, thermalConductivity: 40.0, specificHeat: 5e8);

    private static TetMesh Box(Vector3d origin) =>
        StructuredTetMesh.Box(origin, new Vector3d(10, 10, 10), 2, 2, 2);

    /// <summary>
    /// <b>The thermal analogue of an unrestrained body.</b> With no prescribed temperature
    /// and no convective surface, the conduction matrix is singular with a constant null
    /// space — add any constant to T everywhere and every gradient, hence every flux and
    /// every boundary condition, is unchanged. There is no unique answer, and a plausible
    /// one would be wrong by an unknowable offset.
    /// </summary>
    [Fact]
    public void UndrivenModel_IsRefusedByNameBeforeTheFactorization()
    {
        var model = new ThermalModel(Box(Vector3d.Zero), Metal)
            .HeatFlux(StructuredTetMesh.XMin, 0.5);

        var exception = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
        output.WriteLine(exception.Message);

        Assert.Contains("no prescribed temperature", exception.Message);
        Assert.Contains("convective", exception.Message);
        Assert.Contains("singular", exception.Message);
        // It names the way out, both of them.
        Assert.Contains("one node is enough", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A body with heat going in and nowhere for it to leave has no steady state at all,
    /// and the refusal says so as well — a different fact from "the level is undetermined",
    /// and the one that tells the caller a transient is the honest model.
    /// </summary>
    [Fact]
    public void UndrivenModelWithNetHeatIn_SaysNoSteadyStateExists()
    {
        var model = new ThermalModel(Box(Vector3d.Zero), Metal).Generation(0.01);

        var exception = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
        output.WriteLine(exception.Message);

        Assert.Contains("nowhere to leave", exception.Message);
        Assert.Contains("transient", exception.Message);
    }

    /// <summary>
    /// The check is per CONNECTED BODY: a driven part beside a floating one is singular in
    /// a way no whole-model statement describes, and the message locates the offender.
    /// </summary>
    [Fact]
    public void UndrivenSecondBody_IsNamedWithItsLocation()
    {
        var near = Box(Vector3d.Zero);
        var far = Box(new Vector3d(100, 0, 0));
        var combined = Merge(near, far);

        // Only the first body gets a temperature.
        var mesh = AnalysisMesh.Of(combined);
        var model = new ThermalModel(mesh, Metal);
        foreach (int node in model.NodesOn(f => f.Centroid.X < 50))
            model.TemperatureNode(node, 100);

        var exception = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
        output.WriteLine(exception.Message);

        Assert.Contains("Body 2 of 2", exception.Message);
        // The centroid of the floating box, so a reader can find it in the model.
        Assert.Contains("105", exception.Message);
    }

    /// <summary>Convection alone drives a body — the positive control for the refusal
    /// above, so the rule cannot be over-broad.</summary>
    [Fact]
    public void ConvectionAloneIsEnough_AndIsNotRefused()
    {
        var model = new ThermalModel(Box(Vector3d.Zero), Metal)
            .Generation(0.01)
            .Convection(StructuredTetMesh.ZMax, 0.02, 20);

        var results = ThermalSolver.Solve(model);
        output.WriteLine(
            $"solved: {results.MinTemperature:F3} to {results.MaxTemperature:F3} C, "
            + $"no prescribed nodes ({results.Report.ConstrainedDofs} constrained DOF)");
        Assert.Equal(0, results.Report.ConstrainedDofs);
    }

    /// <summary>
    /// A transient of a perfectly insulated body is NOT refused: the capacity term is
    /// positive definite on its own, so it removes the constant null space the steady
    /// operator has. The body simply holds its energy, which is the right answer.
    /// </summary>
    [Fact]
    public void InsulatedTransient_IsLegalAndConservesEnergy()
    {
        const double initial = 150;
        var mesh = AnalysisMesh.Of(Box(Vector3d.Zero));
        var model = new ThermalModel(mesh, Metal);

        var run = ThermalSolver.SolveTransient(
            model,
            new ThermalTransientOptions(1.0, 10) { InitialTemperature = initial });

        output.WriteLine(
            $"insulated body: {run.Final.MinTemperature:F10} to {run.Final.MaxTemperature:F10} C "
            + $"after {run.Report.Duration} s");
        output.WriteLine(
            $"stored energy {run.Report.InitialStoredEnergy:G10} -> "
            + $"{run.Report.FinalStoredEnergy:G10}");

        double drift = Math.Abs(run.Report.FinalStoredEnergy - run.Report.InitialStoredEnergy)
            / Math.Abs(run.Report.InitialStoredEnergy);
        output.WriteLine($"relative energy drift {drift:E3}");
        Assert.True(drift < 1e-14, $"{drift:E3}");
        foreach (double t in run.Final.Temperature)
            Assert.Equal(initial, t, 10);
    }

    /// <summary>A material with no conductivity makes the matrix identically zero, so the
    /// refusal says the answer would not exist rather than be inaccurate.</summary>
    [Fact]
    public void MaterialWithoutConductivity_IsRefusedNamingIt()
    {
        var plain = new Material("no conductivity stated", 200_000, 0.3, 8e-9);
        var model = new ThermalModel(Box(Vector3d.Zero), plain)
            .Temperature(StructuredTetMesh.XMin, 100);

        var exception = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
        output.WriteLine(exception.Message);

        Assert.Contains("no conductivity stated", exception.Message);
        Assert.Contains("WithThermal", exception.Message);
    }

    /// <summary>A transient needs a heat capacity, and the message says the steady solver
    /// is the call for a body that has none — a body with no capacity IS its steady
    /// state.</summary>
    [Fact]
    public void TransientWithoutHeatCapacity_PointsAtTheSteadySolver()
    {
        var noCapacity = new Material("capacityless", 200_000, 0.3, 0, thermalConductivity: 40);
        var model = new ThermalModel(Box(Vector3d.Zero), noCapacity)
            .Temperature(StructuredTetMesh.XMin, 100);

        var exception = Assert.Throws<FeaException>(
            () => ThermalSolver.SolveTransient(model, new ThermalTransientOptions(1, 5)));
        output.WriteLine(exception.Message);

        Assert.Contains("capacityless", exception.Message);
        Assert.Contains("ThermalSolver.Solve", exception.Message);
    }

    /// <summary>A selector matching nothing is refused where the mistake was made, naming
    /// the tags that DO exist — the structural model's rule, verbatim.</summary>
    [Fact]
    public void SelectorMatchingNothing_IsRefusedAtTheCall()
    {
        var model = new ThermalModel(Box(Vector3d.Zero), Metal);

        var exception = Assert.Throws<FeaException>(
            () => model.Temperature(Facets.Tag(99), 100));
        output.WriteLine(exception.Message);

        Assert.Contains("selected no boundary facets", exception.Message);
        Assert.Contains("carrying tags", exception.Message);
        // The six box-face tags are listed.
        Assert.Contains("0, 1, 2, 3, 4, 5", exception.Message);
    }

    /// <summary>Every node prescribed leaves nothing to solve for, and says so rather than
    /// factoring a zero-by-zero matrix.</summary>
    [Fact]
    public void EveryNodePrescribed_IsRefused()
    {
        var mesh = AnalysisMesh.Of(Box(Vector3d.Zero));
        var model = new ThermalModel(mesh, Metal);
        for (int node = 0; node < mesh.NodeCount; node++)
            model.TemperatureNode(node, 100);

        var exception = Assert.Throws<FeaException>(() => ThermalSolver.Solve(model));
        output.WriteLine(exception.Message);
        Assert.Contains("nothing to solve for", exception.Message);
    }

    /// <summary>
    /// A non-positive film coefficient is refused with the reason: zero is not a condition
    /// (an unmentioned surface is already adiabatic, that being the weak form's natural
    /// boundary condition) and a negative one would make the matrix indefinite.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.01)]
    public void NonPositiveFilmCoefficient_IsRefused(double film)
    {
        var model = new ThermalModel(Box(Vector3d.Zero), Metal);
        var exception = Assert.Throws<FeaException>(
            () => model.Convection(Facets.All, film, 20));
        output.WriteLine(exception.Message);

        Assert.Contains("film coefficient", exception.Message);
        Assert.Contains("adiabatic", exception.Message);
    }

    /// <summary>
    /// A thermal load whose field has the wrong node count is refused, and the message
    /// names the quadratic mid-edge nodes — the actual cause when a linear thermal solve is
    /// pointed at a quadratic structural model.
    /// </summary>
    [Fact]
    public void ThermalLoadWithMismatchedFieldLength_IsRefused()
    {
        var tets = Box(Vector3d.Zero);
        var linear = AnalysisMesh.Of(tets);
        var quadratic = AnalysisMesh.Quadratic(tets);

        var thermal = new ThermalModel(linear, Metal.WithThermal(40, 5e8, 12e-6))
            .Temperature(StructuredTetMesh.XMin, 100)
            .Temperature(StructuredTetMesh.XMax, 20);
        var temperature = ThermalSolver.Solve(thermal);

        var structural = new StructuralModel(quadratic, Metal.WithThermal(40, 5e8, 12e-6))
            .Fix(StructuredTetMesh.XMin);

        var exception = Assert.Throws<FeaException>(
            () => structural.ThermalLoad(temperature.Temperature, 20));
        output.WriteLine(exception.Message);

        Assert.Contains("mid-edge", exception.Message);
        Assert.Contains($"{linear.NodeCount:N0}", exception.Message);
    }

    /// <summary>
    /// Handing a structural model results from a DIFFERENT mesh instance is refused: a
    /// temperature field crosses by node index, and two meshes of the same body can number
    /// their nodes differently, so this is the case that would silently apply each node's
    /// temperature to some other node.
    /// </summary>
    [Fact]
    public void ThermalLoadFromADifferentMeshInstance_IsRefused()
    {
        var tets = Box(Vector3d.Zero);
        var thermalMesh = AnalysisMesh.Of(tets);
        var structuralMesh = AnalysisMesh.Of(tets);   // same geometry, different instance

        var hot = Metal.WithThermal(40, 5e8, 12e-6);
        var thermal = new ThermalModel(thermalMesh, hot)
            .Temperature(StructuredTetMesh.XMin, 100)
            .Temperature(StructuredTetMesh.XMax, 20);
        var temperature = ThermalSolver.Solve(thermal);

        var structural = new StructuralModel(structuralMesh, hot).Fix(StructuredTetMesh.XMin);

        var exception = Assert.Throws<FeaException>(() => structural.ThermalLoad(temperature, 20));
        output.WriteLine(exception.Message);

        Assert.Contains("different AnalysisMesh instance", exception.Message);
        Assert.Contains("node index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A thermal load on a material with no expansion coefficient is refused
    /// rather than silently applying nothing — a zero load AND a stress recovery with
    /// nothing to subtract would look exactly like a model without a thermal load.</summary>
    [Fact]
    public void ThermalLoadWithoutExpansionCoefficient_IsRefused()
    {
        var mesh = AnalysisMesh.Of(Box(Vector3d.Zero));
        var model = new StructuralModel(mesh, Metal).Fix(StructuredTetMesh.XMin);

        var exception = Assert.Throws<FeaException>(() => model.UniformThermalLoad(50));
        output.WriteLine(exception.Message);

        Assert.Contains("thermal expansion", exception.Message);
        Assert.Contains("WithThermalExpansion", exception.Message);
    }

    /// <summary>Transient options validate at construction, where the mistake was made.</summary>
    [Theory]
    [InlineData(0.0, 10)]
    [InlineData(-1.0, 10)]
    [InlineData(1.0, 0)]
    public void InvalidTransientOptions_AreRefusedAtConstruction(double step, int steps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ThermalTransientOptions(step, steps));
    }

    /// <summary>An initial field of the wrong length names the analysis mesh's node
    /// count.</summary>
    [Fact]
    public void InitialFieldOfTheWrongLength_IsRefused()
    {
        var mesh = AnalysisMesh.Of(Box(Vector3d.Zero));
        var model = new ThermalModel(mesh, Metal).Temperature(StructuredTetMesh.XMin, 100);

        var exception = Assert.Throws<FeaException>(() => ThermalSolver.SolveTransient(
            model,
            new ThermalTransientOptions(1, 5) { InitialField = new double[3] }));
        output.WriteLine(exception.Message);

        Assert.Contains("initial field has 3", exception.Message);
        Assert.Contains($"{mesh.NodeCount:N0} nodes", exception.Message);
    }

    /// <summary>Two connected meshes side by side, for the per-body checks.</summary>
    private static TetMesh Merge(TetMesh a, TetMesh b)
    {
        var positions = new List<Vector3d>();
        for (int v = 0; v < a.VertexCount; v++)
            positions.Add(a.Position(v));
        int offset = positions.Count;
        for (int v = 0; v < b.VertexCount; v++)
            positions.Add(b.Position(v));

        var tets = new List<int>();
        var regions = new List<int>();
        for (int t = 0; t < a.TetCount; t++)
        {
            var e = a.GetTet(t);
            tets.AddRange([e.A, e.B, e.C, e.D]);
            regions.Add(0);
        }
        for (int t = 0; t < b.TetCount; t++)
        {
            var e = b.GetTet(t);
            tets.AddRange([e.A + offset, e.B + offset, e.C + offset, e.D + offset]);
            regions.Add(1);
        }

        var facets = new List<TetFacet>();
        for (int f = 0; f < a.BoundaryFacetCount; f++)
        {
            var facet = a.BoundaryFacets[f];
            facets.Add(new TetFacet(facet.V0, facet.V1, facet.V2, facet.Tet, facet.SourceTriangle));
        }
        for (int f = 0; f < b.BoundaryFacetCount; f++)
        {
            var facet = b.BoundaryFacets[f];
            facets.Add(new TetFacet(
                facet.V0 + offset, facet.V1 + offset, facet.V2 + offset,
                facet.Tet + a.TetCount, facet.SourceTriangle));
        }

        return new TetMesh([.. positions], [.. tets], [.. regions], [.. facets]);
    }
}
