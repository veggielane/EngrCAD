using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// A Gauss rule over the reference tetrahedron 0 &lt;= r, s, t and r+s+t &lt;= 1. Weights
/// sum to 1/6 (the reference tetrahedron's volume), so an integral over the physical
/// element is <c>sum_q w_q · f(xi_q) · detJ_q</c>.
/// </summary>
internal readonly struct TetQuadrature
{
    private readonly double[] _points;   // 3 per point
    private readonly double[] _weights;

    private TetQuadrature(double[] points, double[] weights, int degree)
    {
        _points = points;
        _weights = weights;
        Degree = degree;
    }

    /// <summary>Highest polynomial degree the rule integrates exactly.</summary>
    public int Degree { get; }

    /// <summary>Number of quadrature points.</summary>
    public int Count => _weights.Length;

    /// <summary>The q-th point's natural coordinates.</summary>
    public (double R, double S, double T) Point(int q) =>
        (_points[q * 3], _points[q * 3 + 1], _points[q * 3 + 2]);

    /// <summary>The q-th weight.</summary>
    public double Weight(int q) => _weights[q];

    /// <summary>One point at the centroid — exact for degree 1. Everything a straight-sided
    /// LINEAR tetrahedron needs, since its strain-displacement matrix is constant.</summary>
    public static readonly TetQuadrature Degree1 = new(
        [0.25, 0.25, 0.25],
        [1.0 / 6.0],
        1);

    /// <summary>
    /// The classic four-point rule, exact for degree 2:
    /// <c>a = (5 + 3·sqrt5)/20</c>, <c>b = (5 - sqrt5)/20</c>, one coordinate <c>a</c> and
    /// the rest <c>b</c>, equal weights.
    /// <para>This is what a straight-sided QUADRATIC tetrahedron needs and no more: with
    /// mid-edge nodes at exact midpoints the isoparametric map degenerates to an affine
    /// one, so the Jacobian is constant, the strain-displacement matrix is linear in the
    /// natural coordinates, and <c>B' D B</c> is exactly quadratic. That claim is not
    /// taken on trust — <c>TetElementTests</c> asserts the stiffness matrix is unchanged
    /// under <see cref="Degree3"/>, which is precisely the statement that the elements
    /// really are straight-sided.</para>
    /// </summary>
    public static readonly TetQuadrature Degree2 = BuildDegree2();

    /// <summary>
    /// The five-point rule, exact for degree 3 (centroid at weight -4/5, four points at
    /// 9/20). The negative weight makes it a poor production rule, but it is an
    /// INDEPENDENT integrator of everything <see cref="Degree2"/> claims — which is the
    /// only job it has here.
    /// </summary>
    public static readonly TetQuadrature Degree3 = new(
        [
            0.25, 0.25, 0.25,
            0.5, 1.0 / 6.0, 1.0 / 6.0,
            1.0 / 6.0, 0.5, 1.0 / 6.0,
            1.0 / 6.0, 1.0 / 6.0, 0.5,
            1.0 / 6.0, 1.0 / 6.0, 1.0 / 6.0,
        ],
        [
            -0.8 / 6.0,
            0.45 / 6.0, 0.45 / 6.0, 0.45 / 6.0, 0.45 / 6.0,
        ],
        3);

    /// <summary>
    /// Keast's fifteen-point rule, exact for degree 5 — one point at the centroid, four
    /// on the faces' centroids, four near the corners and six near the edge midpoints.
    /// <para>Needed only where the integrand is not a polynomial of the element's own
    /// making: a general <c>BodyForce</c> field, where a cubic load against quadratic
    /// shape functions is already degree 5. Under-integrating a load is a quiet way to
    /// cap a convergence study's order at the quadrature's rather than the element's.</para>
    /// </summary>
    public static readonly TetQuadrature Degree5 = BuildDegree5();

    private static TetQuadrature BuildDegree2()
    {
        double a = (5.0 + 3.0 * Math.Sqrt(5.0)) / 20.0;
        double b = (5.0 - Math.Sqrt(5.0)) / 20.0;
        return new TetQuadrature(
            [
                a, b, b,
                b, a, b,
                b, b, a,
                b, b, b,
            ],
            [1.0 / 24.0, 1.0 / 24.0, 1.0 / 24.0, 1.0 / 24.0],
            2);
    }

    private static TetQuadrature BuildDegree5()
    {
        // Keast (1986). Barycentric groups, weights already summing to 1/6: the centroid;
        // one coordinate 0 with the rest 1/3; one coordinate 8/11 with the rest 1/11; and
        // two coordinates 0.06655015 with two 0.43344985.
        var points = new List<double>();
        var weights = new List<double>();

        // Takes all FOUR barycentric coordinates although only three are stored, so the
        // permutation groups below can be read straight off Keast's published table
        // instead of being re-derived. (r, s, t) = (L1, L2, L3); L0 = 1 - r - s - t.
        void Add(double l0, double l1, double l2, double l3, double w)
        {
            _ = l0;
            points.Add(l1);
            points.Add(l2);
            points.Add(l3);
            weights.Add(w);
        }

        Add(0.25, 0.25, 0.25, 0.25, 0.030283678097089);

        const double third = 1.0 / 3.0;
        double w2 = 0.006026785714286;
        Add(0, third, third, third, w2);
        Add(third, 0, third, third, w2);
        Add(third, third, 0, third, w2);
        Add(third, third, third, 0, w2);

        const double high = 8.0 / 11.0, low = 1.0 / 11.0;
        double w3 = 0.011645249086029;
        Add(high, low, low, low, w3);
        Add(low, high, low, low, w3);
        Add(low, low, high, low, w3);
        Add(low, low, low, high, w3);

        const double p = 0.066550153573664, q = 0.433449846426336;
        double w4 = 0.010949141561386;
        Add(p, p, q, q, w4);
        Add(p, q, p, q, w4);
        Add(p, q, q, p, w4);
        Add(q, p, p, q, w4);
        Add(q, p, q, p, w4);
        Add(q, q, p, p, w4);

        return new TetQuadrature([.. points], [.. weights], 5);
    }

    /// <summary>The rule an element of this order needs — the cheapest one that is exact.</summary>
    public static TetQuadrature For(ElementOrder order) =>
        order == ElementOrder.Linear ? Degree1 : Degree2;

    /// <summary>
    /// The rule a MASS or CAPACITY matrix of this order needs: degree <c>2p</c>, which is
    /// <b>two degrees above</b> <see cref="For"/>'s <c>2(p-1)</c>. Degree 2 for 4-node
    /// elements, degree 5 (the cheapest rule available above 4) for 10-node ones.
    ///
    /// <para><b>Why it is a separate selector and not a comment on <see cref="For"/>.</b>
    /// A stiffness or conductivity integrates <c>grad N · grad N</c> and a mass or capacity
    /// integrates <c>N · N</c>, so the two differ by exactly two degrees — and using the
    /// cheaper rule for the heavier integrand is a SILENT error, not a loud one. Integrate a
    /// 4-node mass with the one-point centroid rule and every one of its sixteen entries
    /// comes out <c>rho·V/16</c>: a rank-one matrix that cannot be factored, whose entries
    /// nevertheless sum to exactly <c>rho·V</c>, the body's true mass. The obvious sanity
    /// check — "does the mass matrix add up to the mass" — passes it. Both
    /// <c>ThermalElementTests</c> and <c>MassMatrixTests</c> pin that with a negative
    /// control.</para>
    /// </summary>
    public static TetQuadrature ForMass(ElementOrder order) =>
        order == ElementOrder.Linear ? Degree2 : Degree5;
}

/// <summary>
/// Shape functions, element stiffness and consistent load vectors for 4-node and 10-node
/// tetrahedra under small-strain isotropic linear elasticity.
///
/// <para><b>The stiffness is assembled in index form, not as B' D B.</b> For an isotropic
/// material the integrand collapses to
/// <c>K_ij^ab = L·N_i,a·N_j,b + M·N_i,b·N_j,a + M·(grad N_i · grad N_j)·delta_ab</c>,
/// which is the same matrix (asserted against an explicit B' D B in the tests) at a
/// fraction of the arithmetic and with the symmetry manifest rather than emergent.</para>
///
/// <para><b>Voigt order is (xx, yy, zz, xy, yz, zx)</b> with engineering shear strains,
/// matching <see cref="Material.Stress"/>.</para>
/// </summary>
internal static class TetElement
{
    /// <summary>Natural coordinates of a linear element's four nodes.</summary>
    private static readonly double[] LinearNodeCoords =
    [
        0, 0, 0,
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
    ];

    /// <summary>Natural coordinates of a quadratic element's ten nodes, in the
    /// <see cref="QuadraticTet"/> ordering.</summary>
    private static readonly double[] QuadraticNodeCoords =
    [
        0, 0, 0,
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
        0.5, 0, 0,      // 4: mid(0,1)
        0.5, 0.5, 0,    // 5: mid(1,2)
        0, 0.5, 0,      // 6: mid(0,2)
        0, 0, 0.5,      // 7: mid(0,3)
        0.5, 0, 0.5,    // 8: mid(1,3)
        0, 0.5, 0.5,    // 9: mid(2,3)
    ];

    /// <summary>Natural coordinates of node <paramref name="node"/> of an element of the
    /// given order — where stress is evaluated for nodal recovery.</summary>
    public static (double R, double S, double T) NodeCoordinates(ElementOrder order, int node)
    {
        var table = order == ElementOrder.Linear ? LinearNodeCoords : QuadraticNodeCoords;
        return (table[node * 3], table[node * 3 + 1], table[node * 3 + 2]);
    }

    /// <summary>Shape-function VALUES at natural coordinates (r, s, t).</summary>
    public static void ShapeValues(ElementOrder order, double r, double s, double t, Span<double> n)
    {
        double l0 = 1.0 - r - s - t, l1 = r, l2 = s, l3 = t;
        if (order == ElementOrder.Linear)
        {
            n[0] = l0;
            n[1] = l1;
            n[2] = l2;
            n[3] = l3;
            return;
        }
        n[0] = l0 * (2.0 * l0 - 1.0);
        n[1] = l1 * (2.0 * l1 - 1.0);
        n[2] = l2 * (2.0 * l2 - 1.0);
        n[3] = l3 * (2.0 * l3 - 1.0);
        n[4] = 4.0 * l0 * l1;
        n[5] = 4.0 * l1 * l2;
        n[6] = 4.0 * l0 * l2;
        n[7] = 4.0 * l0 * l3;
        n[8] = 4.0 * l1 * l3;
        n[9] = 4.0 * l2 * l3;
    }

    /// <summary>Shape-function derivatives with respect to the NATURAL coordinates.</summary>
    private static void ShapeNaturalGradients(
        ElementOrder order, double r, double s, double t, Span<Vector3d> grad)
    {
        // dL0 = (-1,-1,-1), dL1 = (1,0,0), dL2 = (0,1,0), dL3 = (0,0,1).
        if (order == ElementOrder.Linear)
        {
            grad[0] = new Vector3d(-1, -1, -1);
            grad[1] = new Vector3d(1, 0, 0);
            grad[2] = new Vector3d(0, 1, 0);
            grad[3] = new Vector3d(0, 0, 1);
            return;
        }

        double l0 = 1.0 - r - s - t, l1 = r, l2 = s, l3 = t;
        var d0 = new Vector3d(-1, -1, -1);
        var d1 = new Vector3d(1, 0, 0);
        var d2 = new Vector3d(0, 1, 0);
        var d3 = new Vector3d(0, 0, 1);

        grad[0] = d0 * (4.0 * l0 - 1.0);
        grad[1] = d1 * (4.0 * l1 - 1.0);
        grad[2] = d2 * (4.0 * l2 - 1.0);
        grad[3] = d3 * (4.0 * l3 - 1.0);
        grad[4] = (d0 * l1 + d1 * l0) * 4.0;
        grad[5] = (d1 * l2 + d2 * l1) * 4.0;
        grad[6] = (d0 * l2 + d2 * l0) * 4.0;
        grad[7] = (d0 * l3 + d3 * l0) * 4.0;
        grad[8] = (d1 * l3 + d3 * l1) * 4.0;
        grad[9] = (d2 * l3 + d3 * l2) * 4.0;
    }

    /// <summary>
    /// Shape-function gradients with respect to the PHYSICAL coordinates, and the
    /// Jacobian determinant, at natural coordinates (r, s, t).
    /// <para>The Jacobian is built from the full isoparametric map — every node, not just
    /// the four corners — so the routine stays correct if curved (true isoparametric)
    /// elements are ever added. For the straight-sided elements
    /// <see cref="QuadraticTetMesh"/> produces the two agree exactly, because mid-edge
    /// nodes at exact midpoints make the quadratic terms of the map cancel.</para>
    /// </summary>
    /// <returns>False when the Jacobian is singular (a degenerate element).</returns>
    public static bool ShapeGradients(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        double r, double s, double t,
        Span<Vector3d> gradient,
        out double detJ)
    {
        int count = nodePositions.Length;
        Span<Vector3d> natural = stackalloc Vector3d[10];
        ShapeNaturalGradients(order, r, s, t, natural);

        // J[a][b] = d x_a / d xi_b = sum_i x_i,a · dN_i/d xi_b.
        double j00 = 0, j01 = 0, j02 = 0, j10 = 0, j11 = 0, j12 = 0, j20 = 0, j21 = 0, j22 = 0;
        for (int i = 0; i < count; i++)
        {
            var p = nodePositions[i];
            var g = natural[i];
            j00 += p.X * g.X; j01 += p.X * g.Y; j02 += p.X * g.Z;
            j10 += p.Y * g.X; j11 += p.Y * g.Y; j12 += p.Y * g.Z;
            j20 += p.Z * g.X; j21 += p.Z * g.Y; j22 += p.Z * g.Z;
        }

        double c00 = j11 * j22 - j12 * j21;
        double c01 = j12 * j20 - j10 * j22;
        double c02 = j10 * j21 - j11 * j20;
        detJ = j00 * c00 + j01 * c01 + j02 * c02;
        if (detJ == 0 || double.IsNaN(detJ))
        {
            // Exact-zero division guard (the scale-free tier): whether a nonzero
            // determinant is "too small" is the caller's conditioning question, and
            // TetMesh has already refused non-positive elements exactly.
            gradient[..count].Clear();
            return false;
        }

        // Inverse transpose of J, applied to each natural gradient.
        double inv = 1.0 / detJ;
        double i00 = c00 * inv, i01 = (j02 * j21 - j01 * j22) * inv, i02 = (j01 * j12 - j02 * j11) * inv;
        double i10 = c01 * inv, i11 = (j00 * j22 - j02 * j20) * inv, i12 = (j02 * j10 - j00 * j12) * inv;
        double i20 = c02 * inv, i21 = (j01 * j20 - j00 * j21) * inv, i22 = (j00 * j11 - j01 * j10) * inv;

        // dN/dx_a = sum_b (J^-1)_ba · dN/d xi_b.
        for (int i = 0; i < count; i++)
        {
            var g = natural[i];
            gradient[i] = new Vector3d(
                i00 * g.X + i10 * g.Y + i20 * g.Z,
                i01 * g.X + i11 * g.Y + i21 * g.Z,
                i02 * g.X + i12 * g.Y + i22 * g.Z);
        }
        return true;
    }

    /// <summary>
    /// The element stiffness matrix, row-major, of size <c>(3n)²</c> for an element of n
    /// nodes. Cleared on entry.
    /// </summary>
    public static void Stiffness(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        Material material,
        in TetQuadrature rule,
        Span<double> ke)
    {
        int n = nodePositions.Length;
        int dofs = 3 * n;
        ke[..(dofs * dofs)].Clear();

        double lambda = material.Lambda, mu = material.Mu;
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            double weight = rule.Weight(q) * detJ;

            for (int i = 0; i < n; i++)
            {
                var gi = grad[i];
                // Unpacked into locals rather than read through Vector3d's indexer, which
                // is a throwing four-arm switch: the inner pair of loops would dispatch it
                // nine times per (i, j), i.e. about 900 times per 10-node element.
                double gix = gi.X, giy = gi.Y, giz = gi.Z;
                for (int j = 0; j < n; j++)
                {
                    var gj = grad[j];
                    double gjx = gj.X, gjy = gj.Y, gjz = gj.Z;
                    double dot = mu * (gix * gjx + giy * gjy + giz * gjz) * weight;
                    for (int a = 0; a < 3; a++)
                    {
                        double gia = a == 0 ? gix : a == 1 ? giy : giz;
                        double gja = a == 0 ? gjx : a == 1 ? gjy : gjz;
                        int row = (3 * i + a) * dofs + 3 * j;
                        ke[row] += weight * (lambda * gia * gjx + mu * gja * gix);
                        ke[row + 1] += weight * (lambda * gia * gjy + mu * gja * giy);
                        ke[row + 2] += weight * (lambda * gia * gjz + mu * gja * giz);
                        ke[row + a] += dot;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The element CONSISTENT mass matrix <c>M_ij = integral(rho · N_i · N_j dV)</c>,
    /// row-major, n-by-n for an n-node element. Cleared on entry.
    ///
    /// <para><b>A structural mass matrix and a thermal capacity matrix are the SAME
    /// integral</b> — <c>N_i·N_j</c> against a volumetric constant — and this is the one
    /// implementation of it. <see cref="ThermalElement.Capacity"/> asks it with
    /// <c>rho·c</c>; <see cref="ModalSolver"/> asks it with <c>rho</c> and then replicates
    /// each scalar entry onto the 3x3 identity block, because an isotropic inertia couples
    /// no two axes. Writing it twice would be two chances to pick the quadrature rule wrong
    /// in the one place where getting it wrong is silent (see
    /// <see cref="TetQuadrature.ForMass"/>).</para>
    ///
    /// <para>Two properties are worth knowing because they are what the tests check: every
    /// row sums to <c>rho · integral(N_i dV)</c> — the shape functions are a partition of
    /// unity — which for a 10-node element is <b>negative at the corners</b>, the same
    /// <c>-V/20</c> that already surprises people about <see cref="BodyLoadWeights"/>; and
    /// the whole matrix sums to <c>rho·V</c>, the element's actual mass.</para>
    /// </summary>
    /// <param name="order">Element order.</param>
    /// <param name="nodePositions">The element's node positions.</param>
    /// <param name="volumetricConstant">Mass density for a mass matrix, <c>rho·c</c> for a
    /// heat capacity — the only thing the two uses differ in.</param>
    /// <param name="rule">The quadrature rule, which must be <see cref="TetQuadrature.ForMass"/>
    /// or better; the parameter exists so a test can pass a deliberately wrong one.</param>
    /// <param name="me">Output, at least <c>n*n</c> long.</param>
    public static void ConsistentMass(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        double volumetricConstant,
        in TetQuadrature rule,
        Span<double> me)
    {
        int n = nodePositions.Length;
        me[..(n * n)].Clear();
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            ShapeValues(order, r, s, t, shape);
            double weight = rule.Weight(q) * detJ * volumetricConstant;

            for (int i = 0; i < n; i++)
            {
                double wi = weight * shape[i];
                int row = i * n;
                for (int j = 0; j < n; j++)
                    me[row + j] += wi * shape[j];
            }
        }
    }

    /// <summary>
    /// Consistent body-load weights <c>w_i = integral(N_i dV)</c>, one per element node —
    /// multiply by <c>rho·g</c> for gravity.
    /// <para>The numbers are worth knowing before they surprise someone: a linear element
    /// gives V/4 at every node, while a quadratic one gives <b>-V/20 at each corner</b>
    /// and V/5 at each mid-edge node. Negative corner loads under gravity are correct, not
    /// a sign error — they are what makes the consistent load reproduce a quadratic
    /// displacement field exactly, and they sum to V as they must.</para>
    /// </summary>
    public static void BodyLoadWeights(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        in TetQuadrature rule,
        Span<double> weights)
    {
        int n = nodePositions.Length;
        weights[..n].Clear();
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            ShapeValues(order, r, s, t, shape);
            double weight = rule.Weight(q) * detJ;
            for (int i = 0; i < n; i++)
                weights[i] += weight * shape[i];
        }
    }

    /// <summary>
    /// Consistent nodal loads for a POSITION-DEPENDENT body force,
    /// <c>f_i = integral(N_i · b(x) dV)</c>, evaluating <paramref name="field"/> at the
    /// quadrature points of the physical element. Accumulated into
    /// <paramref name="loads"/>, which is cleared on entry.
    /// </summary>
    public static void BodyLoad(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        in TetQuadrature rule,
        Func<Vector3d, Vector3d> field,
        Span<Vector3d> loads)
    {
        int n = nodePositions.Length;
        loads[..n].Clear();
        Span<double> shape = stackalloc double[10];
        Span<Vector3d> grad = stackalloc Vector3d[10];

        for (int q = 0; q < rule.Count; q++)
        {
            var (r, s, t) = rule.Point(q);
            if (!ShapeGradients(order, nodePositions, r, s, t, grad, out double detJ))
                continue;
            ShapeValues(order, r, s, t, shape);

            var x = Vector3d.Zero;
            for (int i = 0; i < n; i++)
                x += nodePositions[i] * shape[i];
            var b = field(x) * (rule.Weight(q) * detJ);

            for (int i = 0; i < n; i++)
                loads[i] += b * shape[i];
        }
    }

    /// <summary>
    /// Consistent surface-load weights <c>w_i = integral(N_i dA)</c> over one boundary
    /// facet, one per facet node. Multiply by a traction vector for the nodal forces.
    /// <para>A 3-node facet gives A/3 at each node. A straight-sided 6-node facet gives
    /// <b>exactly zero at the corners</b> and A/3 at each mid-edge node — the textbook
    /// result that looks like a bug the first time a pressure load is inspected. The
    /// weights sum to A either way, which is what makes a total-force load exact.</para>
    /// </summary>
    public static void FacetLoadWeights(
        ElementOrder order, ReadOnlySpan<Vector3d> facetNodes, Span<double> weights)
    {
        int n = facetNodes.Length;
        weights[..n].Clear();

        // Area from the corner triangle: straight-sided, so mid-edge nodes are coplanar
        // and add nothing.
        var a = facetNodes[0];
        double area = (facetNodes[1] - a).Cross(facetNodes[2] - a).Length * 0.5;
        // Exact-zero semantic test, not a degeneracy threshold: a facet of no area carries
        // no load whatever the traction, while a merely SMALL facet correctly gets a small
        // one — so there is nothing here for an epsilon to protect against.
        if (area == 0)
            return;

        if (order == ElementOrder.Linear)
        {
            double third = area / 3.0;
            weights[0] = third;
            weights[1] = third;
            weights[2] = third;
            return;
        }

        // Degree-2 triangle rule: the three points (2/3, 1/6, 1/6) and permutations, each
        // weighted A/3 — exact for the quadratic shape functions.
        Span<double> shape = stackalloc double[6];
        double w = area / 3.0;
        ReadOnlySpan<double> barycentric =
        [
            2.0 / 3.0, 1.0 / 6.0, 1.0 / 6.0,
            1.0 / 6.0, 2.0 / 3.0, 1.0 / 6.0,
            1.0 / 6.0, 1.0 / 6.0, 2.0 / 3.0,
        ];
        for (int q = 0; q < 3; q++)
        {
            TriangleShapeValues(
                order, barycentric[q * 3], barycentric[q * 3 + 1], barycentric[q * 3 + 2], shape);
            for (int i = 0; i < n; i++)
                weights[i] += w * shape[i];
        }
    }

    /// <summary>
    /// Triangle shape-function values at barycentric coordinates (l0, l1, l2). The
    /// quadratic node order is (V0, V1, V2, M01, M12, M20), matching
    /// <see cref="QuadraticTetFacet"/>.
    /// </summary>
    public static void TriangleShapeValues(
        ElementOrder order, double l0, double l1, double l2, Span<double> n)
    {
        if (order == ElementOrder.Linear)
        {
            n[0] = l0;
            n[1] = l1;
            n[2] = l2;
            return;
        }
        n[0] = l0 * (2.0 * l0 - 1.0);
        n[1] = l1 * (2.0 * l1 - 1.0);
        n[2] = l2 * (2.0 * l2 - 1.0);
        n[3] = 4.0 * l0 * l1;
        n[4] = 4.0 * l1 * l2;
        n[5] = 4.0 * l2 * l0;
    }

    /// <summary>
    /// Strain (Voigt, engineering shear) at natural coordinates (r, s, t) from the
    /// element's nodal displacements, laid out as 3 per node.
    /// </summary>
    public static bool StrainAt(
        ElementOrder order,
        ReadOnlySpan<Vector3d> nodePositions,
        ReadOnlySpan<double> nodalDisplacements,
        double r, double s, double t,
        Span<double> strain)
    {
        int n = nodePositions.Length;
        Span<Vector3d> grad = stackalloc Vector3d[10];
        if (!ShapeGradients(order, nodePositions, r, s, t, grad, out _))
        {
            strain[..6].Clear();
            return false;
        }

        double exx = 0, eyy = 0, ezz = 0, gxy = 0, gyz = 0, gzx = 0;
        for (int i = 0; i < n; i++)
        {
            var g = grad[i];
            double ux = nodalDisplacements[3 * i];
            double uy = nodalDisplacements[3 * i + 1];
            double uz = nodalDisplacements[3 * i + 2];
            exx += g.X * ux;
            eyy += g.Y * uy;
            ezz += g.Z * uz;
            gxy += g.Y * ux + g.X * uy;
            gyz += g.Z * uy + g.Y * uz;
            gzx += g.Z * ux + g.X * uz;
        }
        strain[0] = exx;
        strain[1] = eyy;
        strain[2] = ezz;
        strain[3] = gxy;
        strain[4] = gyz;
        strain[5] = gzx;
        return true;
    }

    /// <summary>A Voigt 6-vector as a symmetric tensor. Shear entries are halved when
    /// <paramref name="engineeringShear"/> is true (the strain convention); stress uses
    /// false, since tau_xy is already the tensor component.</summary>
    public static SymmetricTensor3 ToTensor(ReadOnlySpan<double> voigt, bool engineeringShear)
    {
        double scale = engineeringShear ? 0.5 : 1.0;
        // Voigt order is (xx, yy, zz, xy, yz, zx); SymmetricTensor3 takes (xx, yy, zz, xy, xz, yz).
        return new SymmetricTensor3(
            voigt[0], voigt[1], voigt[2],
            voigt[3] * scale, voigt[5] * scale, voigt[4] * scale);
    }

    /// <summary>The von Mises equivalent of a stress tensor.</summary>
    public static double VonMises(in SymmetricTensor3 s)
    {
        double dxy = s.Xx - s.Yy;
        double dyz = s.Yy - s.Zz;
        double dzx = s.Zz - s.Xx;
        return Math.Sqrt(
            0.5 * (dxy * dxy + dyz * dyz + dzx * dzx)
            + 3.0 * (s.Xy * s.Xy + s.Yz * s.Yz + s.Xz * s.Xz));
    }
}
