using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The patch test: a constant-strain state must be reproduced EXACTLY, to round-off, by
/// every element type. It is the standard correctness gate for a displacement-based
/// formulation, and it catches essentially every assembly, indexing, Jacobian and
/// boundary-condition error outright — a wrong element still converges to something, but
/// it cannot reproduce a linear field exactly.
///
/// <para>Two forms are run, and running both is the point. The <b>displacement</b> patch
/// test prescribes a linear field on the whole boundary and solves for the interior, which
/// exercises assembly and constraint elimination. The <b>traction</b> patch test applies a
/// uniform surface traction against a statically determinate restraint, which additionally
/// exercises the consistent load vectors — the half the first form cannot see.</para>
/// </summary>
public class FeaPatchTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("patch steel", 210_000, 0.3);

    private static TetMesh PatchMesh() =>
        TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(new Vector3d(0, 0, 0), new Vector3d(2.0, 1.5, 1.0))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 0.45 });

    /// <summary>An arbitrary linear displacement field with normal AND shear content, so a
    /// formulation that gets only the diagonal right fails.</summary>
    private static Vector3d LinearField(Vector3d p) => new(
        1.0e-3 + 2.0e-3 * p.X + 0.7e-3 * p.Y - 0.4e-3 * p.Z,
        -0.5e-3 + 0.3e-3 * p.X - 1.1e-3 * p.Y + 0.9e-3 * p.Z,
        0.2e-3 - 0.6e-3 * p.X + 0.5e-3 * p.Y + 1.4e-3 * p.Z);

    /// <summary>The symmetric part of the field's gradient — the strain it must produce.</summary>
    private static SymmetricTensor3 ExpectedStrain() => new(
        2.0e-3, -1.1e-3, 1.4e-3,
        0.5 * (0.7e-3 + 0.3e-3),
        0.5 * (-0.4e-3 - 0.6e-3),
        0.5 * (0.9e-3 + 0.5e-3));

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void DisplacementPatchTest_ReproducesALinearFieldExactly(ElementOrder order)
    {
        var tets = PatchMesh();
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);

        var boundary = model.NodesOn(Facets.All);
        var isBoundary = new bool[mesh.NodeCount];
        foreach (int node in boundary)
        {
            isBoundary[node] = true;
            model.PrescribeNode(node, LinearField(mesh.Position(node)));
        }

        int interior = mesh.NodeCount - boundary.Count;
        Assert.True(interior > 20,
            $"the patch needs genuinely interior nodes to solve for; found {interior}");

        var results = StructuralSolver.Solve(model);

        double extent = mesh.Bounds.Size.Length;
        double reference = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            reference = Math.Max(reference, LinearField(mesh.Position(v)).Length);

        double worstDisplacement = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (isBoundary[v])
                continue;
            var error = results.DisplacementAt(v) - LinearField(mesh.Position(v));
            worstDisplacement = Math.Max(worstDisplacement, error.Length);
        }

        var expected = ExpectedStrain();
        double strainScale = 2.0e-3;
        double worstStrain = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = results.ElementStrain(e);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    worstStrain = Math.Max(worstStrain, Math.Abs(strain[i, j] - expected[i, j]));
            }
        }

        output.WriteLine(
            $"{order}: {mesh.ElementCount:N0} elements, {interior:N0} interior nodes, "
            + $"worst displacement error {worstDisplacement:E3} (field scale {reference:G4}, "
            + $"relative {worstDisplacement / reference:E3}), worst strain error "
            + $"{worstStrain:E3} (relative {worstStrain / strainScale:E3})");
        output.WriteLine(results.Report.ToText());

        Assert.True(worstDisplacement <= 1e-9 * reference,
            $"displacement error {worstDisplacement:E3} against field scale {reference:E3}");
        Assert.True(worstStrain <= 1e-9 * strainScale,
            $"strain error {worstStrain:E3} against strain scale {strainScale:E3}");
        Assert.True(results.Report.RelativeResidual < 1e-9,
            $"solve residual {results.Report.RelativeResidual:E3}");
        Assert.True(extent > 0);
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TractionPatchTest_ReproducesUniformStressExactly(ElementOrder order)
    {
        // A bar pulled by a uniform traction on one end, restrained statically: axial
        // motion removed on the far face, and just enough more to remove the two
        // transverse translations and the roll. The exact elasticity solution is a
        // constant stress state that satisfies every one of those constraints exactly, so
        // the finite-element answer must reproduce it to round-off - which also proves the
        // CONSISTENT LOAD VECTORS are right, including the quadratic facet's zero corner
        // weights.
        const double length = 2.0, width = 1.5, height = 1.0;
        const double sigma = 25.0;

        var tets = TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(length, width, height))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 0.45 });
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);

        model.Fix(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX), Dof.X);
        int origin = FindNode(mesh, Vector3d.Zero);
        int alongY = FindNode(mesh, new Vector3d(0, width, 0));
        model.FixNode(origin, Dof.Y | Dof.Z);
        model.FixNode(alongY, Dof.Z);

        model.Traction(
            Facets.OnPlane(new Vector3d(length, 0, 0), Vector3d.UnitX),
            new Vector3d(sigma, 0, 0));

        var results = StructuralSolver.Solve(model);

        // Every element must carry exactly the uniaxial state.
        double worst = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var s = results.ElementStress(e);
            worst = Math.Max(worst, Math.Abs(s.Xx - sigma));
            worst = Math.Max(worst, Math.Abs(s.Yy));
            worst = Math.Max(worst, Math.Abs(s.Zz));
            worst = Math.Max(worst, Math.Abs(s.Xy));
            worst = Math.Max(worst, Math.Abs(s.Yz));
            worst = Math.Max(worst, Math.Abs(s.Xz));
        }

        // And the displacement must be the closed-form one, measured from the origin node.
        double nu = Steel.PoissonsRatio, e0 = Steel.YoungsModulus;
        double worstDisplacement = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            var p = mesh.Position(v);
            var exact = new Vector3d(sigma / e0 * p.X, -nu * sigma / e0 * p.Y, -nu * sigma / e0 * p.Z);
            worstDisplacement = Math.Max(worstDisplacement, (results.DisplacementAt(v) - exact).Length);
        }
        double displacementScale = sigma / e0 * length;

        double expectedForce = sigma * width * height;
        output.WriteLine(
            $"{order}: {mesh.ElementCount:N0} elements, worst stress error {worst:E3} of {sigma}, "
            + $"worst displacement error {worstDisplacement:E3} of {displacementScale:E3}, "
            + $"applied {results.Report.AppliedForce.X:G8} (exact {expectedForce:G8})");

        Assert.Equal(expectedForce, results.Report.AppliedForce.X, expectedForce * 1e-12);
        Assert.True(worst <= 1e-9 * sigma, $"stress error {worst:E3}");
        Assert.True(worstDisplacement <= 1e-9 * displacementScale,
            $"displacement error {worstDisplacement:E3}");
        Assert.True(results.Report.EquilibriumResidual < 1e-10,
            $"equilibrium residual {results.Report.EquilibriumResidual:E3}");
    }

    internal static int FindNode(AnalysisMesh mesh, Vector3d position, double tolerance = 1e-9)
    {
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (mesh.Position(v).DistanceTo(position) <= tolerance)
                return v;
        }
        throw new Xunit.Sdk.XunitException($"no node at {position}");
    }
}
