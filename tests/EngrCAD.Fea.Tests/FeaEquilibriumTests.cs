using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Rigid-body and equilibrium properties of the ASSEMBLED system — the global counterparts
/// of <see cref="TetElementTests"/>' element-level checks.
/// </summary>
public class FeaEquilibriumTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("equilibrium steel", 210_000, 0.3, 7.85e-9);
    private static readonly Vector3d Size = new(30, 20, 10);

    private static TetMesh Block(int n = 3) =>
        StructuredTetMesh.Box(Vector3d.Zero, Size, 3 * n, 2 * n, n);

    private static AnalysisMesh Analysis(ElementOrder order, TetMesh tets) =>
        order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ARigidMotionPrescribedOnTheBoundary_ProducesNoStrainAndNoEnergy(ElementOrder order)
    {
        // The strongest statement of "the rigid modes carry no energy" that a solver with
        // supports can make: prescribe a rigid motion on the boundary, solve the interior,
        // and the answer must be that same rigid motion everywhere — exactly, since a
        // rigid field is linear and both element types reproduce linear fields exactly.
        var mesh = Analysis(order, Block());
        var model = new StructuralModel(mesh, Steel);

        var axis = new Vector3d(0.3, -0.5, 0.8).Normalized();
        double angle = 1e-4;                          // small, so linearised rotation is the motion
        var offset = new Vector3d(0.7, -0.2, 0.45);
        Vector3d Rigid(Vector3d p) => offset + axis.Cross(p) * angle;

        foreach (int node in model.NodesOn(Facets.All))
            model.PrescribeNode(node, Rigid(mesh.Position(node)));

        var results = StructuralSolver.Solve(model);

        double motionScale = offset.Length + angle * Size.Length;
        double worst = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
            worst = Math.Max(worst, (results.DisplacementAt(v) - Rigid(mesh.Position(v))).Length);

        double worstStrain = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var strain = results.ElementStrain(e);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    worstStrain = Math.Max(worstStrain, Math.Abs(strain[i, j]));
            }
        }

        // The references are the quantities a NON-rigid motion of this size would produce:
        // a strain of order (displacement / extent), and an energy of order E times its
        // square times the volume. Comparing against zero is not an option — the energy is
        // a sum of products of large numbers with near-total cancellation, so its floor is
        // set by the summation's round-off and not by the strain, which is why the two
        // bars below differ by six orders and both are relative.
        double extent = Size.Length;
        double volume = Size.X * Size.Y * Size.Z;
        double strainReference = motionScale / extent;
        double energyReference = Steel.YoungsModulus * strainReference * strainReference * volume;

        output.WriteLine(
            $"{order}: displacement error {worst:E3} of {motionScale:G4} "
            + $"(relative {worst / motionScale:E3}), worst strain {worstStrain:E3} of "
            + $"{strainReference:E3} (relative {worstStrain / strainReference:E3}), strain energy "
            + $"{results.StrainEnergy:E3} of {energyReference:E3} "
            + $"(relative {results.StrainEnergy / energyReference:E3})");

        Assert.True(worst <= 1e-9 * motionScale, $"displacement error {worst:E3}");
        Assert.True(worstStrain <= 1e-11 * strainReference,
            $"strain {worstStrain:E3} under a rigid motion, against {strainReference:E3}");
        Assert.True(results.StrainEnergy <= 1e-11 * energyReference,
            $"strain energy {results.StrainEnergy:E3} under a rigid motion, against {energyReference:E3}");
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ASelfEquilibratedLoad_GivesTheSameStrainsUnderAnyMinimalRestraint(ElementOrder order)
    {
        // A free body under a self-equilibrated load has a unique STRAIN field and a
        // displacement field determined only up to a rigid motion. Two different
        // statically determinate (3-2-1) restraints must therefore give identical strains
        // and identical strain energy, with the reactions at both essentially zero — the
        // practical form of "the rigid modes carry no energy".
        var tets = Block();
        var mesh = Analysis(order, tets);
        const double traction = 40.0;

        StructuralResults Run(bool alternative)
        {
            var model = new StructuralModel(mesh, Steel);
            // Equal and opposite tractions: the load is self-equilibrated by construction.
            model.Traction(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(traction, 0, 0));
            model.Traction(Facets.Tag(StructuredTetMesh.XMin), new Vector3d(-traction, 0, 0));

            // The textbook 3-2-1: one node pinned; a second OFFSET ALONG X takes Y and Z,
            // which removes the turns about Y and Z; a third offset along Y takes Z, which
            // removes the turn about X. The offsets have to be along different axes — a
            // second node offset along Y would contribute nothing to its own Y restraint
            // and the body would still turn, which is exactly what the solver refused
            // when this test first got it wrong.
            if (!alternative)
            {
                model.FixNode(Node(mesh, new Vector3d(0, 0, 0)), Dof.All);
                model.FixNode(Node(mesh, new Vector3d(Size.X, 0, 0)), Dof.Y | Dof.Z);
                model.FixNode(Node(mesh, new Vector3d(0, Size.Y, 0)), Dof.Z);
            }
            else
            {
                // A different, equally valid scheme at the far corner of the block.
                model.FixNode(Node(mesh, Size), Dof.All);
                model.FixNode(Node(mesh, new Vector3d(0, Size.Y, Size.Z)), Dof.Y | Dof.Z);
                model.FixNode(Node(mesh, new Vector3d(Size.X, 0, Size.Z)), Dof.Z);
            }
            return StructuralSolver.Solve(model);
        }

        var a = Run(alternative: false);
        var b = Run(alternative: true);

        double worstStrain = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var da = a.ElementStrain(e);
            var db = b.ElementStrain(e);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    worstStrain = Math.Max(worstStrain, Math.Abs(da[i, j] - db[i, j]));
            }
        }

        double strainScale = traction / Steel.YoungsModulus;
        double loadMagnitude = traction * Size.Y * Size.Z;
        output.WriteLine(
            $"{order}: strain difference {worstStrain:E3} of {strainScale:E3} "
            + $"(relative {worstStrain / strainScale:E3}); energy {a.StrainEnergy:G10} vs "
            + $"{b.StrainEnergy:G10}; reactions {a.Report.ReactionForce.Length:E3} and "
            + $"{b.Report.ReactionForce.Length:E3} against a {loadMagnitude:G6} load");

        Assert.True(worstStrain <= 1e-9 * strainScale,
            $"strains differ by {worstStrain:E3} between two minimal restraints");
        Assert.Equal(a.StrainEnergy, b.StrainEnergy, Math.Abs(a.StrainEnergy) * 1e-9);
        Assert.True(a.Report.ReactionForce.Length <= 1e-9 * loadMagnitude,
            $"reaction {a.Report.ReactionForce.Length:E3} under a self-equilibrated load");
        Assert.True(b.Report.ReactionForce.Length <= 1e-9 * loadMagnitude,
            $"reaction {b.Report.ReactionForce.Length:E3} under a self-equilibrated load");

        // And the answer is the uniform uniaxial state, which is what makes it more than
        // an agreement between two runs of the same possibly-wrong code.
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var stress = a.ElementStress(e);
            Assert.Equal(traction, stress.Xx, traction * 1e-9);
            Assert.Equal(0.0, stress.Yy, traction * 1e-9);
            Assert.Equal(0.0, stress.Xy, traction * 1e-9);
        }
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void TheAnswerIsFrameIndifferent(ElementOrder order)
    {
        // Solve the same problem twice, the second time with the whole model — geometry,
        // loads and supports — rigidly placed somewhere else. Strain energy and peak
        // stress are scalars and must agree to round-off. Nothing else in the suite tests
        // the Jacobian's handling of a general orientation, since every other fixture is
        // axis-aligned.
        var tets = Block();
        double baseline = 0, placed = 0, baselinePeak = 0, placedPeak = 0;

        for (int pass = 0; pass < 2; pass++)
        {
            var transform = pass == 0
                ? Matrix4d.Identity
                : Matrix4d.CreateTranslation(new Vector3d(-17.5, 4.25, 91.0))
                    * Matrix4d.CreateFromAxisAngle(new Vector3d(0.3, -0.5, 0.8).Normalized(), 0.9);
            var mesh = Analysis(order, Transformed(tets, transform));
            var model = new StructuralModel(mesh, Steel);
            model.Fix(StructuredTetMesh.XMin);
            model.Force(
                Facets.Tag(StructuredTetMesh.XMax),
                transform.TransformVector(new Vector3d(0, 0, -5000)));

            var results = StructuralSolver.Solve(model);
            if (pass == 0)
            {
                baseline = results.StrainEnergy;
                baselinePeak = results.MaxVonMises;
            }
            else
            {
                placed = results.StrainEnergy;
                placedPeak = results.MaxVonMises;
            }
        }

        output.WriteLine(
            $"{order}: strain energy {baseline:G12} vs {placed:G12} "
            + $"(relative {Math.Abs(placed / baseline - 1):E3}); peak von Mises "
            + $"{baselinePeak:G12} vs {placedPeak:G12}");

        Assert.Equal(baseline, placed, Math.Abs(baseline) * 1e-9);
        Assert.Equal(baselinePeak, placedPeak, Math.Abs(baselinePeak) * 1e-9);
    }

    [Fact]
    public void GravityReactsTheBodysActualWeight()
    {
        // The consistent body load must integrate to the true weight — including the
        // negative corner weights a quadratic element gives, which is exactly the case
        // where an eyeball check of the nodal forces would call it a bug.
        foreach (var order in new[] { ElementOrder.Linear, ElementOrder.Quadratic })
        {
            var mesh = Analysis(order, Block(2));
            var model = new StructuralModel(mesh, Steel);
            model.Fix(StructuredTetMesh.ZMin);
            model.Gravity(Materials.GravityMillimetres);

            var results = StructuralSolver.Solve(model);
            double volume = Size.X * Size.Y * Size.Z;
            double weight = Steel.Density * volume * Materials.GravityMillimetres.Length;

            output.WriteLine(
                $"{order}: volume {mesh.Volume:F6} (exact {volume}), applied "
                + $"{results.Report.AppliedForce.Z:G8} N, exact weight {-weight:G8} N, "
                + $"reaction {results.Report.ReactionForce.Z:G8} N");

            Assert.Equal(volume, mesh.Volume, volume * 1e-12);
            Assert.Equal(-weight, results.Report.AppliedForce.Z, weight * 1e-12);
            Assert.Equal(weight, results.Report.ReactionForce.Z, weight * 1e-9);
            Assert.True(results.Report.EquilibriumResidual < 1e-10);
        }
    }

    [Fact]
    public void AUniformPressureOverAClosedSurface_HasNoResultant()
    {
        // Hydrostatic loading of a whole body: the pressure integral over a closed
        // surface is exactly zero, so the consistent nodal forces must sum to zero
        // whatever the facets look like. It catches a normal-direction or facet-area sign
        // error that a single loaded face would not.
        foreach (var order in new[] { ElementOrder.Linear, ElementOrder.Quadratic })
        {
            var mesh = Analysis(order, Block(2));
            var model = new StructuralModel(mesh, Steel);
            model.Pressure(Facets.All, 12.5);

            double perFace = 12.5 * Size.X * Size.Y;   // the largest single-face resultant
            output.WriteLine(
                $"{order}: closed-surface pressure resultant {model.AppliedForce.Length:E3} "
                + $"against a per-face {perFace:G6}");
            Assert.True(model.AppliedForce.Length <= 1e-12 * perFace,
                $"resultant {model.AppliedForce} is not zero");
        }
    }

    [Fact]
    public void ForceOverAFaceSet_PreservesTheResultantExactly()
    {
        // A total force distributed as a traction must sum back to itself. For 10-node
        // elements this only works because the consistent weights are exactly zero at the
        // corners and A/3 at the mid-edge nodes — an intuitively "obvious" A/6, A/6, A/6,
        // A/6... split would be off by a factor and this is the test that says so.
        foreach (var order in new[] { ElementOrder.Linear, ElementOrder.Quadratic })
        {
            var mesh = Analysis(order, Block(2));
            var model = new StructuralModel(mesh, Steel);
            var total = new Vector3d(300, -125.5, 40);
            model.Force(Facets.Tag(StructuredTetMesh.ZMax), total);

            var applied = model.AppliedForce;
            output.WriteLine($"{order}: requested {total}, applied {applied}");
            Assert.Equal(total.X, applied.X, Math.Abs(total.X) * 1e-12);
            Assert.Equal(total.Y, applied.Y, Math.Abs(total.Y) * 1e-12);
            Assert.Equal(total.Z, applied.Z, Math.Abs(total.Z) * 1e-12);
        }
    }

    [Fact]
    public void ConstantBodyForceAgreesWithGravity()
    {
        // BodyForce integrates with a degree-5 rule and Gravity with the element's own;
        // both are exact for a constant field, so they must agree to round-off. If they
        // ever stop agreeing, one of the two rules has been changed without the other.
        foreach (var order in new[] { ElementOrder.Linear, ElementOrder.Quadratic })
        {
            var mesh = Analysis(order, Block(2));
            var byGravity = new StructuralModel(mesh, Steel).Gravity(Materials.GravityMillimetres);
            var byField = new StructuralModel(mesh, Steel)
                .BodyForce(_ => Materials.GravityMillimetres * Steel.Density);

            double scale = 0;
            double worst = 0;
            for (int v = 0; v < mesh.NodeCount; v++)
            {
                scale = Math.Max(scale, byGravity.ForceOf(v).Length);
                worst = Math.Max(worst, (byGravity.ForceOf(v) - byField.ForceOf(v)).Length);
            }
            output.WriteLine($"{order}: worst nodal difference {worst:E3} of {scale:E3}");
            Assert.True(worst <= 1e-12 * scale, $"{order}: {worst:E3} vs scale {scale:E3}");
        }
    }

    internal static int Node(AnalysisMesh mesh, Vector3d position, double tolerance = 1e-9)
    {
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (mesh.Position(v).DistanceTo(position) <= tolerance)
                return v;
        }
        throw new Xunit.Sdk.XunitException($"no node at {position}");
    }

    /// <summary>A rigidly placed copy of a tet mesh: positions transformed, connectivity,
    /// regions and facet tags carried over verbatim.</summary>
    internal static TetMesh Transformed(TetMesh mesh, Matrix4d transform)
    {
        var positions = new Vector3d[mesh.VertexCount];
        for (int v = 0; v < positions.Length; v++)
            positions[v] = transform.TransformPoint(mesh.Position(v));

        var tets = new int[mesh.TetCount * 4];
        var regions = new int[mesh.TetCount];
        for (int t = 0; t < mesh.TetCount; t++)
        {
            var e = mesh.GetTet(t);
            tets[4 * t] = e.A;
            tets[4 * t + 1] = e.B;
            tets[4 * t + 2] = e.C;
            tets[4 * t + 3] = e.D;
            regions[t] = mesh.RegionOf(t);
        }
        return new TetMesh(positions, tets, regions, [.. mesh.BoundaryFacets]);
    }
}
