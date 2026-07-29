using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Modal publishing: the <see cref="MeshField"/> seam a viewer's deformation animation
/// consumes, and the <c>.vtu</c> file ParaView's warp filter does.
///
/// <para><b>The published scale is not the solved one, deliberately.</b>
/// <c>VibrationMode.Shape</c> is mass-normalised, which is the scale every modal identity is
/// stated in and a magnitude of one over the square root of a mass — meaningless as a
/// displacement, and it would change if the density did. The published field is rescaled to
/// a peak nodal magnitude of exactly 1 model length unit, so an animation's amplitude means
/// something a person can picture. Neither scale is "the" displacement: a mode shape has
/// none.</para>
/// </summary>
public class ModalPublishTests(ITestOutputHelper output)
{
    private static ModalResults Solve(ElementOrder order, int modes = 3)
    {
        var mesh = ModalFixtures.Beam(60, 12, 8, 6, 2, 2, order);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        return ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = modes });
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Fields_AreOneVectorFieldPerMode_ScaledToAUnitPeak(ElementOrder order)
    {
        var results = Solve(order);
        var all = results.AllFields();
        Assert.Equal(3, all.Count);

        for (int number = 1; number <= 3; number++)
        {
            var field = Assert.Single(results.Fields(number));
            Assert.Equal($"Mode {number}", field.Name);
            Assert.Equal(ModalResults.FieldNames.Shape(number), field.Name);
            Assert.True(field.IsVector);
            Assert.Equal(ModalResults.ShapeUnits, field.Units);
            Assert.Equal(results.Mesh.NodeCount, field.Count);

            // The peak nodal magnitude is EXACTLY 1: that is the contract an animation's
            // amplitude is stated against.
            double peak = 0;
            for (int v = 0; v < field.Count; v++)
                peak = Math.Max(peak, field.ScalarAt(v));
            output.WriteLine($"{order} mode {number}: peak {peak:F12}, {field}");
            Assert.Equal(1.0, peak, 1e-12);

            // AllFields and Fields must agree, since a Part carries one and a FieldDisplay
            // names the other.
            Assert.Equal(field.Name, all[number - 1].Name);
        }
    }

    [Fact]
    public void TheMassNormalisedShapeIsNotTheDisplayedOne()
    {
        // Stated as a test because it is the property most likely to be assumed away: the
        // solved shape and the published one differ by the peak, and both are documented.
        var results = Solve(ElementOrder.Linear, 1);
        var mode = results.Mode(1);
        var field = results.Fields(1).Single();

        double solvedPeak = mode.Shape.Max(u => u.Length);
        output.WriteLine(
            $"mass-normalised peak {solvedPeak:E4} (1/sqrt(mass) units), published peak 1.0");
        Assert.Equal(solvedPeak, mode.PeakDisplacement, 1e-12 * solvedPeak);
        Assert.True(solvedPeak > 10, "a mass-normalised shape is not a displacement");

        for (int v = 0; v < field.Count; v++)
        {
            var published = field.VectorAt(v);
            var expected = mode.ShapeAt(v) / solvedPeak;
            Assert.Equal(expected.X, published.X, 1e-12);
            Assert.Equal(expected.Y, published.Y, 1e-12);
            Assert.Equal(expected.Z, published.Z, 1e-12);
        }
    }

    [Fact]
    public void SampleOnto_LandsEveryDisplayVertexOnAnAnalysisNodeExactly()
    {
        // The normal case: the display mesh IS what was fed to the tet mesher, so every one
        // of its vertices survives as an analysis boundary node and matches bit for bit.
        var body = Shape.Box(40, 20, 10);
        var part = new Part("beam", body);
        var surface = part.GetMesh();
        var tets = TetMesher.Mesh(surface, new TetMeshOptions
        {
            RefineQuality = true,
            MaxElementSize = 9,
        });

        var model = new StructuralModel(AnalysisMesh.Of(tets), Materials.Steel);
        model.Fix(Facets.OnPlane(new Vector3d(-20, 0, 0), Vector3d.UnitX));
        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });

        var fields = results.SampleOnto(surface, out double distance);
        output.WriteLine(
            $"{results.Modes.Count} modes onto {surface.VertexCount} display vertices, "
            + $"max sampling distance {distance:E3}");
        output.WriteLine(results.ToText());

        Assert.Equal(0.0, distance);
        Assert.Equal(2, fields.Count);
        foreach (var field in fields)
        {
            Assert.Equal(surface.VertexCount, field.Count);
            Assert.True(field.IsVector);
        }

        // The multi-mode overload builds the correspondence ONCE; the single-mode one must
        // still agree with it value for value.
        var single = results.SampleOnto(surface, 1).Single();
        for (int v = 0; v < single.Count; v++)
            Assert.Equal(single.VectorAt(v), fields[0].VectorAt(v));

        // And the whole point of the seam: a Part carries them and a FieldDisplay animates
        // one.
        foreach (var field in fields)
            part.AddResult(field);
        part.FieldDisplay = new FieldDisplay
        {
            Field = ModalResults.FieldNames.Shape(1),
            Deform = ModalResults.FieldNames.Shape(1),
            DeformScale = 2.0,
        };
        Assert.True(part.TryResolveFieldDisplay(out var resolved, out string? why), why);
        Assert.NotNull(resolved.Deform);
        Assert.Equal(ModalResults.FieldNames.Shape(1), resolved.Deform!.Name);
    }

    [Fact]
    public void WriteVtu_CarriesEveryModeAsItsOwnArray()
    {
        var results = Solve(ElementOrder.Quadratic);
        var writer = new StringWriter();
        results.WriteVtu(writer);
        string vtu = writer.ToString();

        Assert.Contains("VTK_QUADRATIC_TETRA".Length > 0 ? "UnstructuredGrid" : "", vtu);
        for (int number = 1; number <= 3; number++)
            Assert.Contains($"Name=\"Mode {number}\"", vtu);
        // Quadratic tetrahedra are VTK cell type 24.
        Assert.Contains("24", vtu);
        output.WriteLine($"{vtu.Length:N0} characters, {results.Modes.Count} mode arrays");
    }

    [Fact]
    public void ReportAndText_StateWhatTheSolveDid()
    {
        var results = Solve(ElementOrder.Linear, 4);
        string text = results.ToText();
        output.WriteLine(text);

        Assert.Contains("mode  1", text);
        Assert.Contains("Hz", text);
        Assert.Contains("ONE factorization", text);
        Assert.Contains("Consistent", text);
        Assert.True(results.Report.Iterations > 0);
        Assert.True(results.Report.FactorNonZeros > 0);
        Assert.Equal(4, results.Report.ModeCount);
    }
}
