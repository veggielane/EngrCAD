using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The conduction element matrices against closed forms and against INDEPENDENT
/// quadrature rules.
///
/// <para><b>Every "the cheap rule is exact" claim gets a negative control.</b> Asserting
/// that two rules agree proves nothing on its own — they would also agree if the code
/// under test ignored the rule it was handed. So each such test is paired with a case that
/// makes the two rules genuinely DISAGREE, which is what shows the assertion has
/// teeth.</para>
/// </summary>
public class ThermalElementTests(ITestOutputHelper output)
{
    private static readonly Vector3d[] UnitTet =
    [
        new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1),
    ];

    /// <summary>A general (not axis-aligned, not unit) tetrahedron, so an error that
    /// cancels on a symmetric element cannot hide.</summary>
    private static readonly Vector3d[] SkewTet =
    [
        new(0.3, -0.2, 0.1), new(2.7, 0.4, -0.3), new(0.9, 3.1, 0.6), new(-0.4, 0.8, 2.2),
    ];

    private static Vector3d[] QuadraticNodes(Vector3d[] corners)
    {
        var nodes = new Vector3d[10];
        Array.Copy(corners, nodes, 4);
        nodes[4] = (corners[0] + corners[1]) * 0.5;
        nodes[5] = (corners[1] + corners[2]) * 0.5;
        nodes[6] = (corners[0] + corners[2]) * 0.5;
        nodes[7] = (corners[0] + corners[3]) * 0.5;
        nodes[8] = (corners[1] + corners[3]) * 0.5;
        nodes[9] = (corners[2] + corners[3]) * 0.5;
        return nodes;
    }

    private static double Volume(Vector3d[] corners) =>
        TetMesh.SignedVolume(corners[0], corners[1], corners[2], corners[3]);

    private static double[] Conductivity(
        ElementOrder order, Vector3d[] nodes, double k, TetQuadrature rule)
    {
        int n = nodes.Length;
        var ke = new double[n * n];
        ThermalElement.Conductivity(order, nodes, k, rule, ke);
        return ke;
    }

    private static double[] Capacity(
        ElementOrder order, Vector3d[] nodes, double rhoC, TetQuadrature rule)
    {
        int n = nodes.Length;
        var ce = new double[n * n];
        ThermalElement.Capacity(order, nodes, rhoC, rule, ce);
        return ce;
    }

    private static double MaxRelativeDifference(double[] a, double[] b)
    {
        double scale = 0;
        foreach (double v in a)
            scale = Math.Max(scale, Math.Abs(v));
        if (scale == 0)
            return 0;
        double worst = 0;
        for (int i = 0; i < a.Length; i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]) / scale);
        return worst;
    }

    // ---- conductivity ----------------------------------------------------------------

    /// <summary>
    /// A conductivity matrix annihilates a CONSTANT temperature field, exactly. This is the
    /// thermal patch test at element level: a body at one temperature conducts no heat,
    /// whatever its shape, so every row of K must sum to zero.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Conductivity_AnnihilatesAConstantField(ElementOrder order)
    {
        var nodes = order == ElementOrder.Linear ? SkewTet : QuadraticNodes(SkewTet);
        int n = nodes.Length;
        var ke = Conductivity(order, nodes, 50.0, ThermalElement.ConductivityRule(order));

        double scale = 0;
        foreach (double v in ke)
            scale = Math.Max(scale, Math.Abs(v));

        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++)
                sum += ke[i * n + j];
            worst = Math.Max(worst, Math.Abs(sum) / scale);
        }
        output.WriteLine($"{order}: worst row sum {worst:E3} relative to |K|max = {scale:G6}");
        Assert.True(worst < 1e-14, $"row sums {worst:E3}");
    }

    /// <summary>
    /// The 4-node conductivity matrix against its closed form
    /// <c>K_ij = k·V · grad(L_i) · grad(L_j)</c>, where the barycentric gradients are the
    /// inward face-normal-over-3V vectors. Computed here from the geometry directly rather
    /// than through the element's Jacobian, so the two arithmetics are independent.
    /// </summary>
    [Fact]
    public void LinearConductivity_MatchesTheClosedForm()
    {
        const double k = 37.5;
        var c = SkewTet;
        double volume = Volume(c);
        var ke = Conductivity(ElementOrder.Linear, c, k, TetQuadrature.Degree1);

        // grad(L_i) points from face i towards vertex i, with magnitude 1/height. Built
        // from the face's own area vector: grad L_i = -A_i / (3V) with A_i the OUTWARD area
        // vector of the face opposite i.
        var gradient = new Vector3d[4];
        int[][] faces = [[1, 2, 3], [0, 3, 2], [0, 1, 3], [0, 2, 1]];
        for (int i = 0; i < 4; i++)
        {
            var a = c[faces[i][0]];
            var area = (c[faces[i][1]] - a).Cross(c[faces[i][2]] - a) * 0.5;
            gradient[i] = area / (-3.0 * volume);
        }

        double worst = 0, scale = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                double expected = k * volume * gradient[i].Dot(gradient[j]);
                scale = Math.Max(scale, Math.Abs(expected));
                worst = Math.Max(worst, Math.Abs(ke[i * 4 + j] - expected));
            }
        }
        output.WriteLine($"volume {volume:G8}, worst |K - closed form| {worst:E3} on |K|max {scale:G6}");
        Assert.True(worst / scale < 1e-13, $"{worst / scale:E3}");
    }

    /// <summary>
    /// The degree-2 rule is exact for a STRAIGHT-SIDED quadratic element's conductivity —
    /// asserted against the independent degree-3 rule — and the CURVED case is the negative
    /// control that gives the claim its meaning: displace one mid-edge node off the
    /// midpoint and the two rules must disagree, because the Jacobian is no longer
    /// constant.
    /// </summary>
    [Fact]
    public void QuadraticConductivity_Degree2IsExactOnlyBecauseElementsAreStraightSided()
    {
        var straight = QuadraticNodes(SkewTet);
        var degree2 = Conductivity(ElementOrder.Quadratic, straight, 50.0, TetQuadrature.Degree2);
        var degree3 = Conductivity(ElementOrder.Quadratic, straight, 50.0, TetQuadrature.Degree3);
        double agreement = MaxRelativeDifference(degree2, degree3);

        var curved = QuadraticNodes(SkewTet);
        curved[5] += new Vector3d(0.35, -0.25, 0.30);   // mid(1,2), pushed off the midpoint
        var curved2 = Conductivity(ElementOrder.Quadratic, curved, 50.0, TetQuadrature.Degree2);
        var curved3 = Conductivity(ElementOrder.Quadratic, curved, 50.0, TetQuadrature.Degree3);
        double disagreement = MaxRelativeDifference(curved2, curved3);

        output.WriteLine($"straight-sided: degree 2 vs 3 agree to {agreement:E3}");
        output.WriteLine($"curved (one mid-edge node moved): they differ by {disagreement:E3}");

        Assert.True(agreement < 1e-13, $"straight-sided rules disagree by {agreement:E3}");
        // The negative control. Without it, a Conductivity that ignored its rule argument
        // would pass the assertion above.
        Assert.True(disagreement > 1e-3,
            $"the curved element's rules agree to {disagreement:E3}, so the test above proves nothing");
    }

    // ---- capacity --------------------------------------------------------------------

    /// <summary>
    /// The 4-node consistent capacity matrix is <c>rho·c·V/20 · (2 on the diagonal, 1 off)</c>
    /// — the textbook result — and the ONE-POINT rule that is exact for the conductivity is
    /// the negative control: it produces a rank-one matrix with every entry
    /// <c>rho·c·V/16</c>, whose TOTAL is still exactly <c>rho·c·V</c>.
    ///
    /// <para>That last fact is the point of the test. A capacity matrix is routinely
    /// sanity-checked by "does it sum to the body's heat capacity", and this one does while
    /// being singular — so a solver that used the conductivity's rule for its capacity
    /// would pass that check and then fail to invert.</para>
    /// </summary>
    [Fact]
    public void LinearCapacity_NeedsADegree2Rule_AndTheDegree1RuleIsSingularWithTheRightTotal()
    {
        const double rhoC = 3.611;
        double volume = Volume(SkewTet);
        var exact = Capacity(ElementOrder.Linear, SkewTet, rhoC, TetQuadrature.Degree2);
        var cheap = Capacity(ElementOrder.Linear, SkewTet, rhoC, TetQuadrature.Degree1);

        double diagonal = rhoC * volume / 10.0, off = rhoC * volume / 20.0;
        double worst = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
                worst = Math.Max(worst, Math.Abs(exact[i * 4 + j] - (i == j ? diagonal : off)));
        }
        output.WriteLine(
            $"volume {volume:G8}: degree-2 capacity matches rho.c.V/20 x (2,1) to {worst:E3}");
        Assert.True(worst / diagonal < 1e-13, $"{worst / diagonal:E3}");

        double totalExact = exact.Sum(), totalCheap = cheap.Sum();
        output.WriteLine(
            $"totals: degree 2 = {totalExact:G10}, degree 1 = {totalCheap:G10}, "
            + $"rho.c.V = {rhoC * volume:G10}");
        Assert.True(Math.Abs(totalExact - rhoC * volume) / (rhoC * volume) < 1e-14);
        // The negative control's first half: the cheap rule's TOTAL is right.
        Assert.True(Math.Abs(totalCheap - rhoC * volume) / (rhoC * volume) < 1e-14);

        // ...and its second half: every entry is identical, so the matrix is rank one.
        double uniform = rhoC * volume / 16.0;
        double spread = cheap.Max() - cheap.Min();
        output.WriteLine(
            $"degree-1 entries are all {cheap[0]:G10} (expected rho.c.V/16 = {uniform:G10}), "
            + $"spread {spread:E3} -> rank 1, singular");
        Assert.True(Math.Abs(cheap[0] - uniform) / uniform < 1e-14);
        Assert.True(spread / uniform < 1e-13, "the degree-1 capacity is not the rank-one matrix");
        Assert.True(MaxRelativeDifference(exact, cheap) > 0.1,
            "the two rules agree, so the exactness claim above proves nothing");
    }

    /// <summary>
    /// The capacity matrix's ROW SUMS are <c>rho·c·integral(N_i dV)</c>, by the partition of
    /// unity — checked against <see cref="TetElement.BodyLoadWeights"/>, which computes that
    /// integral by an entirely separate route for gravity loads.
    ///
    /// <para>It is also where a 10-node element's corner rows come out <b>negative</b>
    /// (<c>-V/20</c>), which is why row-sum lumping is not available for quadratic elements:
    /// it would give four nodes a negative heat capacity.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void Capacity_RowSumsMatchTheBodyLoadWeights(ElementOrder order)
    {
        const double rhoC = 2.5;
        var nodes = order == ElementOrder.Linear ? SkewTet : QuadraticNodes(SkewTet);
        int n = nodes.Length;
        double volume = Volume(SkewTet);

        var ce = Capacity(order, nodes, rhoC, ThermalElement.CapacityRule(order));
        var weights = new double[n];
        TetElement.BodyLoadWeights(order, nodes, TetQuadrature.For(order), weights);

        double worst = 0;
        var sums = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++)
                sum += ce[i * n + j];
            sums[i] = sum;
            worst = Math.Max(worst, Math.Abs(sum - rhoC * weights[i]));
        }
        output.WriteLine($"{order}, volume {volume:G8}, rho.c.V = {rhoC * volume:G8}");
        output.WriteLine($"  row sums: {string.Join(", ", sums.Select(s => s.ToString("G6")))}");
        output.WriteLine($"  worst |row sum - rho.c.(integral N_i)| = {worst:E3}");
        Assert.True(worst / (rhoC * volume) < 1e-13, $"{worst:E3}");

        Assert.True(Math.Abs(ce.Sum() - rhoC * volume) / (rhoC * volume) < 1e-13);

        if (order == ElementOrder.Quadratic)
        {
            // The documented surprise, pinned: a 10-node element's corner row sums are
            // -rho.c.V/20, so row-sum lumping would hand four nodes a negative capacity.
            for (int i = 0; i < 4; i++)
            {
                Assert.True(sums[i] < 0, $"corner row {i} sums to {sums[i]:G6}, expected negative");
                Assert.True(
                    Math.Abs(sums[i] + rhoC * volume / 20.0) / (rhoC * volume) < 1e-13,
                    $"corner row {i} is {sums[i]:G6}, expected {-rhoC * volume / 20.0:G6}");
            }
            output.WriteLine(
                $"  corner rows are {sums[0]:G6} = -rho.c.V/20 -> row-sum lumping would be negative");
        }
    }

    // ---- convection surface matrix ---------------------------------------------------

    /// <summary>
    /// The 3-node convective surface matrix is the textbook <c>h·A/12 · (2 on the diagonal,
    /// 1 off)</c>, and the degree-4 rule the production code uses reproduces it exactly.
    /// The degree-5 rule is the independent check for both element orders.
    /// </summary>
    [Fact]
    public void LinearFacetConvection_MatchesTheClosedForm()
    {
        const double h = 0.025;
        Vector3d[] facet = [new(0.2, 0.1, -0.3), new(3.4, 0.6, 0.2), new(1.1, 2.9, 1.4)];
        double area = (facet[1] - facet[0]).Cross(facet[2] - facet[0]).Length * 0.5;

        var he = new double[9];
        ThermalElement.FacetConvection(
            ElementOrder.Linear, facet, h, TriangleQuadrature.Degree4, he);

        double diagonal = h * area / 6.0, off = h * area / 12.0;
        double worst = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
                worst = Math.Max(worst, Math.Abs(he[i * 3 + j] - (i == j ? diagonal : off)));
        }
        output.WriteLine($"area {area:G8}: worst |H - h.A/12 x (2,1)| = {worst:E3}");
        Assert.True(worst / diagonal < 1e-13, $"{worst / diagonal:E3}");
        Assert.True(Math.Abs(he.Sum() - h * area) / (h * area) < 1e-14,
            "the surface matrix does not sum to h.A");
    }

    /// <summary>
    /// The degree-4 rule is exact for a 6-node facet's convection matrix, checked against
    /// degree 5 — and the matrix sums to <c>h·A</c>, which is what makes an isothermal body
    /// lose exactly <c>h·A·(T - T_inf)</c>.
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void FacetConvection_Degree4AgreesWithDegree5AndSumsToHA(ElementOrder order)
    {
        const double h = 0.025;
        Vector3d[] corners = [new(0.2, 0.1, -0.3), new(3.4, 0.6, 0.2), new(1.1, 2.9, 1.4)];
        var facet = order == ElementOrder.Linear
            ? corners
            : [
                corners[0], corners[1], corners[2],
                (corners[0] + corners[1]) * 0.5,
                (corners[1] + corners[2]) * 0.5,
                (corners[2] + corners[0]) * 0.5,
            ];
        double area = (corners[1] - corners[0]).Cross(corners[2] - corners[0]).Length * 0.5;

        int m = facet.Length;
        var four = new double[m * m];
        var five = new double[m * m];
        ThermalElement.FacetConvection(order, facet, h, TriangleQuadrature.Degree4, four);
        ThermalElement.FacetConvection(order, facet, h, TriangleQuadrature.Degree5, five);

        double agreement = MaxRelativeDifference(four, five);
        output.WriteLine(
            $"{order}: degree 4 vs 5 agree to {agreement:E3}; sum {four.Sum():G10} against "
            + $"h.A = {h * area:G10}");
        Assert.True(agreement < 1e-13, $"{agreement:E3}");
        Assert.True(Math.Abs(four.Sum() - h * area) / (h * area) < 1e-13);
    }

    /// <summary>
    /// The thermal-expansion load is SELF-EQUILIBRATED for any temperature field and any
    /// element — its nodal forces sum to exactly zero, because the shape functions are a
    /// partition of unity so their gradients sum to zero.
    /// <para>That is what keeps the solver's global equilibrium check meaningful through a
    /// coupled solve: a thermal load adds nothing to the applied resultant.</para>
    /// </summary>
    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ThermalExpansionLoad_SumsToZero(ElementOrder order)
    {
        var nodes = order == ElementOrder.Linear ? SkewTet : QuadraticNodes(SkewTet);
        int n = nodes.Length;
        // A non-uniform field, so the cancellation is not the trivial one.
        var deltaT = new double[n];
        for (int i = 0; i < n; i++)
            deltaT[i] = 20.0 + 13.0 * nodes[i].X - 7.5 * nodes[i].Y + 4.25 * nodes[i].Z;

        var loads = new Vector3d[n];
        ThermalElement.ThermalExpansionLoad(
            order, nodes, deltaT, 175_000, 12e-6, TetQuadrature.Degree3, loads);

        var sum = Vector3d.Zero;
        double scale = 0;
        foreach (var f in loads)
        {
            sum += f;
            scale = Math.Max(scale, f.Length);
        }
        output.WriteLine($"{order}: |sum of nodal loads| {sum.Length:E3} on |f|max {scale:G6}");
        Assert.True(sum.Length / scale < 1e-13, $"{sum.Length / scale:E3}");
    }
}
