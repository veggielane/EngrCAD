using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// A Gauss rule over the reference TRIANGLE (barycentric l0 + l1 + l2 = 1), weights
/// summing to 1 so an integral over a physical facet is
/// <c>area · sum_q w_q · f(l_q)</c>.
///
/// <para>Only one rule is needed and it is the higher of the two jobs: the convection
/// surface matrix <c>integral(h·N_i·N_j dA)</c> is degree 4 for 6-node facets, so a
/// degree-4 rule serves it and is exact for the 3-node case several times over. The
/// facet LOAD integral <c>integral(N_i dA)</c> keeps its own closed form in
/// <see cref="TetElement.FacetLoadWeights"/> — the two are not unified because that one
/// is exact by construction and this one is a quadrature, and pretending otherwise would
/// hide which claim rests on what.</para>
/// </summary>
internal readonly struct TriangleQuadrature
{
    private readonly double[] _points;   // 3 barycentric coordinates per point
    private readonly double[] _weights;

    private TriangleQuadrature(double[] points, double[] weights, int degree)
    {
        _points = points;
        _weights = weights;
        Degree = degree;
    }

    /// <summary>Highest polynomial degree the rule integrates exactly.</summary>
    public int Degree { get; }

    /// <summary>Number of quadrature points.</summary>
    public int Count => _weights.Length;

    /// <summary>The q-th point's barycentric coordinates.</summary>
    public (double L0, double L1, double L2) Point(int q) =>
        (_points[q * 3], _points[q * 3 + 1], _points[q * 3 + 2]);

    /// <summary>The q-th weight (weights sum to 1).</summary>
    public double Weight(int q) => _weights[q];

    /// <summary>
    /// The six-point rule exact for degree 4 — two symmetric orbits, all weights positive.
    /// Strang and Fix / Dunavant's table; the weights are stated as fractions of the
    /// triangle's area, so they sum to 1.
    /// </summary>
    public static readonly TriangleQuadrature Degree4 = BuildDegree4();

    /// <summary>
    /// The seven-point rule exact for degree 5 (centroid plus two orbits) — an
    /// INDEPENDENT integrator of everything <see cref="Degree4"/> claims, which is the
    /// only job it has here.
    /// </summary>
    public static readonly TriangleQuadrature Degree5 = BuildDegree5();

    private static TriangleQuadrature BuildDegree4()
    {
        const double a1 = 0.816847572980459, b1 = 0.091576213509771;
        const double a2 = 0.108103018168070, b2 = 0.445948490915965;
        const double w1 = 0.109951743655322, w2 = 0.223381589678011;
        return new TriangleQuadrature(
            [
                a1, b1, b1,
                b1, a1, b1,
                b1, b1, a1,
                a2, b2, b2,
                b2, a2, b2,
                b2, b2, a2,
            ],
            [w1, w1, w1, w2, w2, w2],
            4);
    }

    private static TriangleQuadrature BuildDegree5()
    {
        double r = Math.Sqrt(15.0);
        double a1 = (6.0 - r) / 21.0, b1 = (9.0 + 2.0 * r) / 21.0;
        double a2 = (6.0 + r) / 21.0, b2 = (9.0 - 2.0 * r) / 21.0;
        double w1 = (155.0 - r) / 1200.0, w2 = (155.0 + r) / 1200.0;
        return new TriangleQuadrature(
            [
                1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0,
                b1, a1, a1,
                a1, b1, a1,
                a1, a1, b1,
                b2, a2, a2,
                a2, b2, a2,
                a2, a2, b2,
            ],
            [9.0 / 40.0, w1, w1, w1, w2, w2, w2],
            5);
    }
}

/// <summary>
/// Element matrices for transient heat conduction on the same 4- and 10-node tetrahedra
/// <see cref="TetElement"/> serves: the conductivity (the "stiffness" of the thermal
/// problem), the capacity (its "mass"), and the surface matrix a convective boundary
/// contributes.
///
/// <para><b>One degree of freedom per node, and that is the whole structural
/// difference.</b> The constitutive law is Fourier's <c>q = -k·grad T</c>, so the weak
/// form is <c>integral(k·grad N_i · grad N_j) T_j = …</c> — the same shape-function
/// gradients elasticity already needs, contracted once instead of carrying a 3x3 block
/// per node pair. Nothing here recomputes a Jacobian or a shape function;
/// <see cref="TetElement.ShapeGradients"/> and <see cref="TetElement.ShapeValues"/> are
/// asked, so a mesh either works for both physics or fails identically in both.</para>
///
/// <para><b>The quadrature degrees are NOT the structural ones, and getting that wrong is
/// silent.</b> A conductivity matrix integrates <c>grad N · grad N</c>, degree
/// <c>2(p-1)</c> — the same as a stiffness, so the same rule serves. A capacity matrix
/// integrates <c>N·N</c>, degree <c>2p</c>, which is two degrees higher. Using the
/// conductivity's rule for the capacity is not a small error: for a 4-node element the
/// one-point centroid rule makes every entry <c>rho·c·V/16</c>, a RANK-ONE matrix that is
/// singular — while its total is still exactly <c>rho·c·V</c>, so a "the capacity sums to
/// the right thing" check passes it. <c>ThermalElementTests</c> measures precisely
/// that.</para>
/// </summary>
internal static class ThermalElement
{
    /// <summary>The rule a CONDUCTIVITY matrix of this element order needs — the cheapest
    /// exact one, and the same rule the stiffness uses, since both integrands are
    /// <c>grad N · grad N</c>.</summary>
    public static TetQuadrature ConductivityRule(ElementOrder order) => TetQuadrature.For(order);

    /// <summary>
    /// The rule a CAPACITY matrix of this element order needs. Degree <c>2p</c>, two
    /// higher than the conductivity's: degree 2 for 4-node elements and degree 5 (the
    /// cheapest available rule above 4) for 10-node ones.
    /// </summary>
    public static TetQuadrature CapacityRule(ElementOrder order) =>
        order == ElementOrder.Linear ? TetQuadrature.Degree2 : TetQuadrature.Degree5;

    /// <summary>
    /// The element conductivity matrix <c>K_ij = integral(k · grad N_i · grad N_j dV)</c>,
    /// row-major, n-by-n for an n-node element. Cleared on entry.
    /// </summary>
    public static void Conductivity(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        double conductivity,
        in TetQuadrature rule,
        Span<double> ke)
    {
        int n = nodePositions.Length;
        ke[..(n * n)].Clear();
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!TetElement.ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            double weight = rule.Weight(q) * detJ * conductivity;

            for (int i = 0; i < n; i++)
            {
                // Unpacked into locals rather than read through Vector3d's indexer, which
                // is a throwing four-arm switch — the same reason TetElement.Stiffness
                // does it.
                var gi = grad[i];
                double gix = gi.X, giy = gi.Y, giz = gi.Z;
                int row = i * n;
                for (int j = 0; j < n; j++)
                {
                    var gj = grad[j];
                    ke[row + j] += weight * (gix * gj.X + giy * gj.Y + giz * gj.Z);
                }
            }
        }
    }

    /// <summary>
    /// The element CONSISTENT capacity matrix
    /// <c>C_ij = integral(rho·c · N_i · N_j dV)</c>, row-major, n-by-n. Cleared on entry.
    ///
    /// <para>Consistent, not lumped, and the reasoning is in <see cref="ThermalSolver"/>.
    /// Two properties are worth knowing because they are what the tests check: every row
    /// sums to <c>rho·c · integral(N_i dV)</c> (because the shape functions are a
    /// partition of unity), which for a 10-node element is <b>negative at the corners</b>
    /// — the same <c>-V/20</c> that already surprises people about
    /// <see cref="TetElement.BodyLoadWeights"/>; and the whole matrix sums to
    /// <c>rho·c·V</c>, the body's actual heat capacity.</para>
    /// </summary>
    public static void Capacity(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        double volumetricHeatCapacity,
        in TetQuadrature rule,
        Span<double> ce)
    {
        int n = nodePositions.Length;
        ce[..(n * n)].Clear();
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!TetElement.ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            TetElement.ShapeValues(order, r, s, t, shape);
            double weight = rule.Weight(q) * detJ * volumetricHeatCapacity;

            for (int i = 0; i < n; i++)
            {
                double wi = weight * shape[i];
                int row = i * n;
                for (int j = 0; j < n; j++)
                    ce[row + j] += wi * shape[j];
            }
        }
    }

    /// <summary>
    /// The surface matrix a convective facet contributes,
    /// <c>H_ij = integral(h · N_i · N_j dA)</c>, row-major, m-by-m for an m-node facet.
    /// Cleared on entry.
    ///
    /// <para>This is the one structurally new thing conduction has that elasticity does
    /// not: a boundary condition that lands in the MATRIX as well as the load vector.
    /// Newton's law of cooling <c>q = h(T - T_inf)</c> splits into a term proportional to
    /// the unknown (here) and a known supply (<see cref="FacetLoad"/>), and the two must
    /// be applied together or the answer is a body cooling to the wrong place.</para>
    ///
    /// <para>Area comes from the facet's CORNER triangle, so a 6-node facet's mid-edge
    /// nodes are assumed coplanar — which they are, by
    /// <see cref="QuadraticTetMesh"/>'s exact-midpoint construction, the same assumption
    /// that makes the 4-point tetrahedral rule exact.</para>
    /// </summary>
    public static void FacetConvection(
        ElementOrder order,
        ReadOnlySpan<Vector3d> facetNodes,
        double filmCoefficient,
        in TriangleQuadrature rule,
        Span<double> he)
    {
        int m = facetNodes.Length;
        he[..(m * m)].Clear();

        var a = facetNodes[0];
        double area = (facetNodes[1] - a).Cross(facetNodes[2] - a).Length * 0.5;
        // Exact-zero semantic test, matching FacetLoadWeights: a facet of no area carries
        // no convection whatever the film coefficient, and a merely SMALL one correctly
        // gets a small matrix — so there is nothing for an epsilon to protect against.
        if (area == 0)
            return;

        Span<double> shape = stackalloc double[6];
        double scale = area * filmCoefficient;
        for (int q = 0; q < rule.Count; q++)
        {
            var (l0, l1, l2) = rule.Point(q);
            TetElement.TriangleShapeValues(order, l0, l1, l2, shape);
            double weight = scale * rule.Weight(q);
            for (int i = 0; i < m; i++)
            {
                double wi = weight * shape[i];
                int row = i * m;
                for (int j = 0; j < m; j++)
                    he[row + j] += wi * shape[j];
            }
        }
    }

    /// <summary>
    /// The temperature GRADIENT at natural coordinates (r, s, t) from the element's nodal
    /// temperatures. The heat flux is <c>-k</c> times this.
    /// </summary>
    /// <returns>False for a degenerate element, with the gradient zeroed.</returns>
    public static bool GradientAt(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        ReadOnlySpan<double> nodalTemperature,
        double r, double s, double t,
        out Vector3d gradient)
    {
        int n = nodePositions.Length;
        Span<Vector3d> grad = stackalloc Vector3d[10];
        if (!TetElement.ShapeGradients(order, nodePositions, r, s, t, grad, out _))
        {
            gradient = Vector3d.Zero;
            return false;
        }

        var sum = Vector3d.Zero;
        for (int i = 0; i < n; i++)
            sum += grad[i] * nodalTemperature[i];
        gradient = sum;
        return true;
    }

    /// <summary>
    /// A scalar field's value at natural coordinates (r, s, t), interpolated from the
    /// element's own nodal values with its own shape functions — how a temperature field
    /// reaches a thermal-expansion load and a stress recovery.
    /// </summary>
    public static double InterpolateAt(
        ElementOrder order, ReadOnlySpan<double> nodalValues, double r, double s, double t)
    {
        Span<double> shape = stackalloc double[10];
        TetElement.ShapeValues(order, r, s, t, shape);
        double sum = 0;
        for (int i = 0; i < nodalValues.Length; i++)
            sum += shape[i] * nodalValues[i];
        return sum;
    }

    /// <summary>
    /// Consistent nodal loads for a convective facet's KNOWN half,
    /// <c>f_i = integral(h · T_inf · N_i dA)</c> — the supply term Newton's law of cooling
    /// contributes beside <see cref="FacetConvection"/>.
    /// <para>It delegates to <see cref="TetElement.FacetLoadWeights"/> rather than
    /// integrating again: <c>integral(N_i dA)</c> is the same quantity a pressure load
    /// already needs, and asking the same code is what keeps a 6-node facet's zero corner
    /// weights from being rediscovered differently here.</para>
    /// </summary>
    public static void FacetLoad(
        ElementOrder order,
        ReadOnlySpan<Vector3d> facetNodes,
        double perArea,
        Span<double> loads)
    {
        int m = facetNodes.Length;
        TetElement.FacetLoadWeights(order, facetNodes, loads);
        for (int i = 0; i < m; i++)
            loads[i] *= perArea;
    }

    /// <summary>
    /// Consistent nodal loads for a POSITION-DEPENDENT volumetric heat generation,
    /// <c>f_i = integral(N_i · qdot(x) dV)</c>, evaluating <paramref name="field"/> at the
    /// physical element's quadrature points. Accumulated into <paramref name="loads"/>,
    /// which is cleared on entry.
    /// <para>Integrated with the caller's rule, which <see cref="ThermalModel"/> makes
    /// degree 5 for the same reason <c>StructuralModel.BodyForce</c> does: a caller's
    /// field is not a polynomial of the element's making, and under-integrating a load
    /// caps a convergence study at the QUADRATURE's order while looking exactly like a
    /// formulation defect.</para>
    /// </summary>
    public static void Generation(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        in TetQuadrature rule,
        Func<Vector3d, double> field,
        Span<double> loads)
    {
        int n = nodePositions.Length;
        loads[..n].Clear();
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!TetElement.ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            TetElement.ShapeValues(order, r, s, t, shape);

            var x = Vector3d.Zero;
            for (int i = 0; i < n; i++)
                x += nodePositions[i] * shape[i];
            double generated = field(x) * (rule.Weight(q) * detJ);

            for (int i = 0; i < n; i++)
                loads[i] += generated * shape[i];
        }
    }

    /// <summary>
    /// Consistent nodal FORCES from a thermal-expansion initial strain
    /// <c>eps0 = alpha·dT·(1,1,1,0,0,0)</c>:
    /// <c>f_i^a = integral((3·lambda + 2·mu)·alpha·dT · dN_i/dx_a dV)</c>.
    /// Accumulated into <paramref name="loads"/>, cleared on entry.
    ///
    /// <para><b>The isotropic collapse is what makes this three lines.</b> The general
    /// form is <c>integral(B' · D · eps0 dV)</c>, but for an isotropic material
    /// <c>D·eps0</c> is a pure hydrostatic stress <c>(3·lambda + 2·mu)·alpha·dT</c> on the
    /// diagonal — which is <c>E/(1 - 2·nu)·alpha·dT</c>, i.e. three times the bulk modulus
    /// — so contracting it with B leaves one gradient component per degree of freedom and
    /// no matrix product at all.</para>
    ///
    /// <para><b>The load is self-equilibrated by construction</b>, and that is not a
    /// coincidence to be checked afterwards: summing over an element's nodes gives
    /// <c>integral(… · grad(sum_i N_i) dV)</c>, and the shape functions are a partition of
    /// unity, so the gradient of their sum is exactly zero. A uniform temperature rise on
    /// an unrestrained body therefore produces zero resultant, which is what lets the
    /// existing equilibrium check keep working through a coupled solve.</para>
    /// </summary>
    public static void ThermalExpansionLoad(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        ReadOnlySpan<double> nodalDeltaT,
        double modulus,
        double expansion,
        in TetQuadrature rule,
        Span<Vector3d> loads)
    {
        int n = nodePositions.Length;
        loads[..n].Clear();
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!TetElement.ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            TetElement.ShapeValues(order, r, s, t, shape);

            double deltaT = 0;
            for (int i = 0; i < n; i++)
                deltaT += shape[i] * nodalDeltaT[i];

            double weight = rule.Weight(q) * detJ * modulus * expansion * deltaT;
            for (int i = 0; i < n; i++)
                loads[i] += grad[i] * weight;
        }
    }
}
