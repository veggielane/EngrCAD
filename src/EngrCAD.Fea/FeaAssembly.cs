using EngrCAD.Core;
using EngrCAD.Core.Solvers;

namespace EngrCAD.Fea;

/// <summary>
/// The matrix bookkeeping every structural eigen-solve shares: the free/restrained index
/// map, the whole-model stiffness assembly, the free-free reduction and a matrix sum.
///
/// <para><b>Shared for the reason <see cref="FeaGuards"/> is shared</b> — the modal solver
/// and the buckling solver must build the SAME <c>K</c> from the same model, at the same
/// quadrature points, in the same summation order, or a comparison between a natural
/// frequency and a buckling factor computed from one model is comparing two different
/// discretizations. Restating the loop would be a second chance to disagree, which is the
/// defect this project has now paid for three times in other guises.</para>
///
/// <para>The whole matrix is assembled and reduced afterwards rather than assembled
/// directly into the reduced numbering as <see cref="StructuralSolver"/> does, because the
/// eigen-solvers need whole-model quantities beside the reduced ones — a mass matrix's
/// total is a statement about every degree of freedom including the restrained ones, and a
/// rigid motion is stated over all of them.</para>
/// </summary>
internal static class FeaAssembly
{
    /// <summary>
    /// The DOF index map: the slot each of the <c>3·nodes</c> degrees of freedom takes in
    /// the reduced system, or <c>-1</c> where a support removed it.
    ///
    /// <para><b>Monotone in the DOF index by construction</b> — free DOFs take increasing
    /// slots — which is what lets <see cref="Reduce"/> feed an upper-triangle entry of the
    /// full matrix straight into an upper-triangle builder.</para>
    /// </summary>
    public static int[] ReducedIndices(StructuralModel model, out int freeCount)
    {
        var mesh = model.Mesh;
        var reduced = new int[3 * mesh.NodeCount];
        freeCount = 0;
        for (int node = 0; node < mesh.NodeCount; node++)
        {
            var restraint = model.RestraintOf(node);
            for (int axis = 0; axis < 3; axis++)
            {
                bool fixedHere = ((int)restraint & (1 << axis)) != 0;
                reduced[3 * node + axis] = fixedHere ? -1 : freeCount++;
            }
        }
        return reduced;
    }

    /// <summary>The FULL stiffness matrix over every degree of freedom (symmetric upper).</summary>
    public static PackedSparseMatrix Stiffness(StructuralModel model, in TetQuadrature rule)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        int elementDofs = 3 * perElement;
        var builder = new SparseMatrixBuilder(3 * mesh.NodeCount, 3 * mesh.NodeCount);
        var ke = new double[elementDofs * elementDofs];
        var positions = new Vector3d[perElement];
        var dofs = new int[elementDofs];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
            {
                positions[i] = mesh.Position(nodes[i]);
                for (int a = 0; a < 3; a++)
                    dofs[3 * i + a] = 3 * nodes[i] + a;
            }
            TetElement.Stiffness(mesh.Order, positions, model.ElasticityOf(e), rule, ke);

            for (int i = 0; i < elementDofs; i++)
            {
                int ri = dofs[i];
                int row = i * elementDofs;
                for (int j = 0; j < elementDofs; j++)
                {
                    double v = ke[row + j];
                    // Exact-zero skip: a structurally absent entry is absent from the
                    // sparsity pattern too, which is what CSR means.
                    if (v == 0)
                        continue;
                    int rj = dofs[j];
                    if (ri <= rj)
                        builder.Add(ri, rj, v);
                }
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>
    /// The FULL geometric stiffness <c>scale·Kg</c> over every degree of freedom, built from
    /// a reference solve's own recovered stress (symmetric upper).
    ///
    /// <para><b>The scale is applied here rather than by a second pass</b>, because both
    /// consumers want a signed multiple and neither wants <c>Kg</c> itself:
    /// <see cref="BucklingSolver"/> asks for <c>-Kg</c> (its eigenproblem is
    /// <c>K phi = lambda·(-Kg) phi</c>) and <see cref="ModalSolver"/>'s stress stiffening
    /// asks for <c>+s·Kg</c> to add to <c>K</c>. Forming the matrix and negating or scaling
    /// it afterwards would be a second place for a sign to be lost.</para>
    ///
    /// <para>The per-node-pair integral is a SCALAR replicated onto the 3x3 identity block,
    /// exactly as the mass matrix's is — see <see cref="TetElement.GeometricStiffness"/> for
    /// why that is a fact about the physics rather than a shortcut.</para>
    /// </summary>
    public static PackedSparseMatrix Geometric(
        StructuralResults reference, in TetQuadrature rule, double scale)
    {
        var mesh = reference.Mesh;
        int perElement = mesh.NodesPerElement;
        var builder = new SparseMatrixBuilder(3 * mesh.NodeCount, 3 * mesh.NodeCount);
        var kg = new double[perElement * perElement];
        var positions = new Vector3d[perElement];
        var displacements = new double[3 * perElement];
        // Six Voigt components per quadrature point; the largest rule here has 15 points.
        var stress = new double[6 * rule.Count];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            reference.Gather(e, positions, displacements);
            for (int q = 0; q < rule.Count; q++)
            {
                var (r, s, t) = rule.Point(q);
                reference.VoigtStressAt(
                    e, positions, displacements, r, s, t, stress.AsSpan(q * 6, 6));
            }
            TetElement.GeometricStiffness(mesh.Order, positions, stress, rule, kg);

            for (int i = 0; i < perElement; i++)
            {
                int row = i * perElement;
                for (int j = 0; j < perElement; j++)
                {
                    double v = scale * kg[row + j];
                    // Exact-zero skip, as everywhere else: an absent entry is absent from the
                    // sparsity pattern.
                    if (v == 0)
                        continue;
                    for (int a = 0; a < 3; a++)
                    {
                        int ri = 3 * nodes[i] + a, rj = 3 * nodes[j] + a;
                        if (ri <= rj)
                            builder.Add(ri, rj, v);
                    }
                }
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>The free-free block of a full matrix (see <see cref="ReducedIndices"/> for
    /// the monotonicity this relies on).</summary>
    public static PackedSparseMatrix Reduce(PackedSparseMatrix full, int[] reduced, int freeCount)
    {
        var builder = new SparseMatrixBuilder(freeCount, freeCount);
        for (int row = 0; row < full.Rows; row++)
        {
            int ri = reduced[row];
            if (ri < 0)
                continue;
            var columns = full.RowColumns(row);
            var values = full.RowValues(row);
            for (int e = 0; e < columns.Length; e++)
            {
                int rj = reduced[columns[e]];
                if (rj < 0)
                    continue;
                builder.Add(ri, rj, values[e]);
            }
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary><c>a + coefficient·b</c>, both symmetric-upper with the same shape rules.</summary>
    public static PackedSparseMatrix Combine(
        PackedSparseMatrix a, PackedSparseMatrix b, double coefficient) =>
        // Delegating rather than restating, and bit-identically: multiplying a finite double
        // by exactly 1.0 returns it unchanged, so the entries added here are the same bits in
        // the same order as the two-loop form always produced.
        Combine(a, 1.0, b, coefficient);

    /// <summary>
    /// <c>aCoefficient·a + bCoefficient·b</c>, both symmetric-upper with the same shape rules.
    ///
    /// <para>The two-coefficient form exists for the transient solver's effective stiffness,
    /// which is <c>(1+alpha)(1 + a1·beta_R)·K + (a0 + (1+alpha)·a1·alpha_R)·M</c> — neither
    /// matrix enters at unit weight, so scaling one afterwards would need a third pass over
    /// the entries to buy nothing.</para>
    /// </summary>
    public static PackedSparseMatrix Combine(
        PackedSparseMatrix a, double aCoefficient,
        PackedSparseMatrix b, double bCoefficient)
    {
        var builder = new SparseMatrixBuilder(a.Rows, a.Columns);
        for (int row = 0; row < a.Rows; row++)
        {
            var columns = a.RowColumns(row);
            var values = a.RowValues(row);
            for (int e = 0; e < columns.Length; e++)
                builder.Add(row, columns[e], aCoefficient * values[e]);
        }
        for (int row = 0; row < b.Rows; row++)
        {
            var columns = b.RowColumns(row);
            var values = b.RowValues(row);
            for (int e = 0; e < columns.Length; e++)
                builder.Add(row, columns[e], bCoefficient * values[e]);
        }
        return builder.ToSymmetricUpper();
    }

    /// <summary>
    /// The FULL mass matrix over every degree of freedom, and the body's total mass.
    ///
    /// <para><b>The scalar integral is asked, not restated.</b>
    /// <see cref="TetElement.ConsistentMass"/> returns the n-by-n matrix
    /// <c>integral(rho·N_i·N_j dV)</c>; an isotropic inertia couples no two axes, so the 3x3
    /// block for a node pair is that scalar times the identity and the assembly loop simply
    /// writes it on the three diagonal positions.</para>
    ///
    /// <para><b>The total mass comes from the matrix's own entries.</b> Summing every entry
    /// of the consistent element matrix gives exactly <c>rho·V</c>, because the shape
    /// functions are a partition of unity — so the reported mass is the assembly's own
    /// arithmetic rather than a second computation from densities and volumes that could
    /// disagree in the last bits. It is also what the lumping schemes are normalised
    /// against, which is what makes them mass-preserving by construction.</para>
    ///
    /// <para><b>Shared for the reason <see cref="Stiffness"/> is shared</b>: the modal solver
    /// and the transient solver must build the SAME M, or a frequency measured from a time
    /// history and one returned by an eigen-solve are answers about two different
    /// discretizations — which is precisely the cross-check the two are asked to pass.</para>
    /// </summary>
    public static (PackedSparseMatrix Matrix, double TotalMass) Mass(
        StructuralModel model, in TetQuadrature rule,
        MassLumping lumping = MassLumping.Consistent)
    {
        var mesh = model.Mesh;
        int perElement = mesh.NodesPerElement;
        var builder = new SparseMatrixBuilder(3 * mesh.NodeCount, 3 * mesh.NodeCount);
        var me = new double[perElement * perElement];
        var positions = new Vector3d[perElement];
        var diagonal = new double[perElement];
        double totalMass = 0;

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);
            TetElement.ConsistentMass(
                mesh.Order, positions, model.MaterialOf(e).Density, rule, me);

            double elementMass = 0;
            for (int i = 0; i < perElement * perElement; i++)
                elementMass += me[i];
            totalMass += elementMass;

            if (lumping == MassLumping.Consistent)
            {
                for (int i = 0; i < perElement; i++)
                {
                    int row = i * perElement;
                    for (int j = 0; j < perElement; j++)
                    {
                        double v = me[row + j];
                        if (v == 0)
                            continue;
                        for (int a = 0; a < 3; a++)
                        {
                            int ri = 3 * nodes[i] + a, rj = 3 * nodes[j] + a;
                            if (ri <= rj)
                                builder.Add(ri, rj, v);
                        }
                    }
                }
                continue;
            }

            if (lumping == MassLumping.RowSum)
            {
                for (int i = 0; i < perElement; i++)
                {
                    double sum = 0;
                    int row = i * perElement;
                    for (int j = 0; j < perElement; j++)
                        sum += me[row + j];
                    diagonal[i] = sum;
                }
            }
            else
            {
                // HRZ: the consistent matrix's own diagonal, scaled to preserve the mass.
                double trace = 0;
                for (int i = 0; i < perElement; i++)
                {
                    diagonal[i] = me[i * perElement + i];
                    trace += diagonal[i];
                }
                // Exact-zero division guard: a weightless element (already refused at the
                // model level) or a degenerate one contributes nothing to scale.
                double scale = trace > 0 ? elementMass / trace : 0;
                for (int i = 0; i < perElement; i++)
                    diagonal[i] *= scale;
            }

            for (int i = 0; i < perElement; i++)
            {
                if (diagonal[i] == 0)
                    continue;
                for (int a = 0; a < 3; a++)
                {
                    int r = 3 * nodes[i] + a;
                    builder.Add(r, r, diagonal[i]);
                }
            }
        }

        return (builder.ToSymmetricUpper(), totalMass);
    }

    /// <summary>The unit rigid translation along one axis, over the FREE degrees of freedom
    /// — the influence vector a participation factor is measured against.</summary>
    public static double[] InfluenceVector(int freeCount, int[] reduced, int nodeCount, int axis)
    {
        var influence = new double[freeCount];
        for (int node = 0; node < nodeCount; node++)
        {
            int r = reduced[3 * node + axis];
            if (r >= 0)
                influence[r] = 1.0;
        }
        return influence;
    }
}
