using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// One rigid motion of one connected body that the supports permit at zero strain energy —
/// a null vector of the assembled stiffness matrix, described rather than counted.
/// </summary>
/// <param name="Body">Zero-based connected-component index.</param>
/// <param name="BodyCount">How many connected bodies the mesh has.</param>
/// <param name="BodyNodeCount">Nodes in this body.</param>
/// <param name="BodyCentroid">This body's node centroid — the point the rotation is
/// measured about.</param>
/// <param name="Translation">The motion's translation part.</param>
/// <param name="Rotation">The motion's rotation vector about <paramref name="BodyCentroid"/>.</param>
/// <param name="Description">A readable name: "translation along (…)" or "rotation about the
/// axis through (…) along (…)".</param>
/// <param name="Field">The motion as a displacement field over EVERY node of the mesh —
/// zero outside this body, and exactly zero on restrained degrees of freedom.</param>
internal readonly record struct RigidMotion(
    int Body,
    int BodyCount,
    int BodyNodeCount,
    Vector3d BodyCentroid,
    Vector3d Translation,
    Vector3d Rotation,
    string Description,
    Vector3d[] Field);

/// <summary>
/// Finds the rigid-body motions a model's supports fail to remove, <b>per connected
/// body</b>, and describes each one.
///
/// <para><b>Shared because two solvers need the same answer for opposite reasons.</b>
/// <see cref="StructuralSolver"/> refuses a model with any surviving motion — a linear
/// static solve of an unrestrained body has no unique answer. <see cref="ModalSolver"/>
/// keeps them: they are the zero-frequency modes, a legitimate part of the answer, and they
/// must be separated from the elastic modes rather than reported as the first six of them.
/// Restating the computation in the modal solver would be two chances to disagree about
/// what "unrestrained" means, in the one place where one solver's error message and the
/// other's results have to describe the same physics.</para>
///
/// <para><b>The null space and not merely its dimension.</b> The six rigid modes of a body
/// are built over its own nodes, normalised, and restricted to the constrained degrees of
/// freedom; a combination of them that vanishes on every constrained DOF is a motion the
/// supports permit at zero energy. Those combinations are the null space of the modes' Gram
/// matrix over the constrained DOFs, taken from a Jacobi eigen-decomposition. A rank count
/// answers "how many motions survive" but not "which", and the difference is not cosmetic:
/// an earlier version reported which candidate modes a pivoted Cholesky had failed to
/// eliminate, which for a model pinned at a single node named three TRANSLATIONS when the
/// surviving motions were three ROTATIONS about that node.</para>
///
/// <para>Per BODY rather than globally, because a fully fixed part beside a floating one is
/// singular in a way no whole-model rigid mode describes.</para>
/// </summary>
internal static class RigidBodyModes
{
    /// <summary>
    /// Relative eigenvalue floor for the null-space test. A Gram matrix's eigenvalues are
    /// SQUARED singular values, so 1e-12 here is a 1e-6 relative singular value — the floor
    /// the sketch-constraint solver arrived at, after 1e-8 proved to sit below the
    /// elimination's own round-off and report impossible ranks.
    /// </summary>
    public const double RankEigenvalueFloor = 1e-12;

    /// <summary>
    /// Every rigid motion the supports permit, over every connected body, in body order.
    /// An empty list means the model is fully restrained and its stiffness matrix is
    /// positive definite.
    /// </summary>
    /// <param name="mesh">The analysis mesh.</param>
    /// <param name="restraintOf">The restraint mask on a node.</param>
    public static List<RigidMotion> Surviving(AnalysisMesh mesh, Func<int, Dof> restraintOf)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(restraintOf);

        var found = new List<RigidMotion>();
        var (component, componentCount) = mesh.ConnectedComponents();

        var nodesOf = new List<int>[componentCount];
        for (int c = 0; c < componentCount; c++)
            nodesOf[c] = [];
        for (int v = 0; v < mesh.NodeCount; v++)
            nodesOf[component[v]].Add(v);

        // Hoisted out of the loops below: six-element scratch, reused per component and
        // per node (CA2014 — a stackalloc inside a loop grows the frame every pass).
        Span<double> norm = stackalloc double[6];
        Span<int> candidates = stackalloc int[6];
        Span<Vector3d> value = stackalloc Vector3d[6];

        for (int c = 0; c < componentCount; c++)
        {
            var nodes = nodesOf[c];
            var centroid = Vector3d.Zero;
            foreach (int v in nodes)
                centroid += mesh.Position(v);
            centroid /= nodes.Count;

            for (int k = 0; k < 6; k++)
            {
                double sum = 0;
                foreach (int v in nodes)
                    sum += Mode(k, mesh.Position(v), centroid).LengthSquared;
                norm[k] = Math.Sqrt(sum);
            }

            // A mode whose norm is negligible against the largest is no motion at all
            // (a single-node body has no rotations, a collinear one has one degenerate
            // rotation); drop it rather than dividing by it. Relative, not exact-zero,
            // because the norms carry the model's own scale.
            int candidateCount = 0;
            double largest = 0;
            for (int k = 0; k < 6; k++)
                largest = Math.Max(largest, norm[k]);
            for (int k = 0; k < 6; k++)
            {
                if (norm[k] > largest * 1e-12)
                    candidates[candidateCount++] = k;
            }
            if (candidateCount == 0)
                continue;

            var gram = new double[candidateCount * candidateCount];
            foreach (int v in nodes)
            {
                var restraint = restraintOf(v);
                if (restraint == Dof.None)
                    continue;
                var p = mesh.Position(v);
                for (int i = 0; i < candidateCount; i++)
                    value[i] = Mode(candidates[i], p, centroid) / norm[candidates[i]];
                for (int axis = 0; axis < 3; axis++)
                {
                    if (((int)restraint & (1 << axis)) == 0)
                        continue;
                    for (int i = 0; i < candidateCount; i++)
                    {
                        for (int j = 0; j < candidateCount; j++)
                            gram[i * candidateCount + j] += value[i][axis] * value[j][axis];
                    }
                }
            }

            var (eigenvalues, eigenvectors) = SmallSymmetricEigen.Solve(gram, candidateCount);
            double dominant = eigenvalues.Max();
            var nullVectors = new List<double[]>();
            for (int i = 0; i < candidateCount; i++)
            {
                if (eigenvalues[i] <= dominant * RankEigenvalueFloor)
                {
                    var vector = new double[candidateCount];
                    for (int j = 0; j < candidateCount; j++)
                        vector[j] = eigenvectors[j * candidateCount + i];
                    nullVectors.Add(vector);
                }
            }
            if (nullVectors.Count == 0)
                continue;

            // THIS BODY's extent, not the whole model's: the translation-versus-rotation
            // verdict below weighs |w|·extent against |t|, so on a model holding one large
            // part and one small one the whole-model extent would decide the small part's
            // description by the large part's size. Computed once per body rather than
            // once per surviving mode.
            var bodyBounds = Aabb.Empty;
            foreach (int v in nodes)
                bodyBounds = bodyBounds.Union(mesh.Position(v));
            double bodyExtent = bodyBounds.Size.Length;

            // Each null vector IS a surviving motion, so it can be described rather than
            // guessed at: unpack it back into a translation and a rotation about the
            // body's centroid, then name the axis it turns about.
            foreach (var vector in nullVectors)
            {
                var translation = Vector3d.Zero;
                var rotation = Vector3d.Zero;
                for (int i = 0; i < candidateCount; i++)
                {
                    int mode = candidates[i];
                    double coefficient = vector[i] / norm[mode];
                    var unit = mode % 3 == 0 ? Vector3d.UnitX
                             : mode % 3 == 1 ? Vector3d.UnitY
                             : Vector3d.UnitZ;
                    if (mode < 3)
                        translation += unit * coefficient;
                    else
                        rotation += unit * coefficient;
                }

                // The motion as a displacement field. Restrained components are set to
                // EXACTLY zero rather than left at the null-space solve's residual: the
                // modal solver deflates against this vector, and a component on a
                // constrained degree of freedom would deflate a direction the reduced
                // system does not have.
                var motion = new Vector3d[mesh.NodeCount];
                foreach (int v in nodes)
                {
                    var u = translation + rotation.Cross(mesh.Position(v) - centroid);
                    var restraint = restraintOf(v);
                    if (restraint != Dof.None)
                    {
                        u = new Vector3d(
                            restraint.HasFlag(Dof.X) ? 0 : u.X,
                            restraint.HasFlag(Dof.Y) ? 0 : u.Y,
                            restraint.HasFlag(Dof.Z) ? 0 : u.Z);
                    }
                    motion[v] = u;
                }

                found.Add(new RigidMotion(
                    c, componentCount, nodes.Count, centroid, translation, rotation,
                    DescribeMotion(translation, rotation, centroid, bodyExtent),
                    motion));
            }
        }

        return found;
    }

    /// <summary>Mode k evaluated at node position p: three translations, three rotations
    /// about the body's own centroid.</summary>
    private static Vector3d Mode(int k, Vector3d p, Vector3d centre) => k switch
    {
        0 => Vector3d.UnitX,
        1 => Vector3d.UnitY,
        2 => Vector3d.UnitZ,
        3 => Vector3d.UnitX.Cross(p - centre),
        4 => Vector3d.UnitY.Cross(p - centre),
        _ => Vector3d.UnitZ.Cross(p - centre),
    };

    /// <summary>
    /// Names a rigid motion given its translation and its rotation about
    /// <paramref name="centre"/>. A pure translation is reported as one; anything with a
    /// rotation is reported as a turn about a located AXIS, since "rotation about Y" is
    /// useless without saying where the axis is.
    /// <para>An axis is a LINE, so which point on it to quote is a choice: this reports
    /// <c>centre + (w x t)/|w|^2</c>, the point where the motion's translation
    /// perpendicular to the axis vanishes — equivalently the axis's closest approach to
    /// the body's own centroid. A model pinned at one node therefore reports axes through
    /// that node only when the node IS the centroid; otherwise it reports the same lines
    /// through a different point on each.</para>
    /// </summary>
    private static string DescribeMotion(
        Vector3d translation, Vector3d rotation, Vector3d centre, double extent)
    {
        // Scale-free comparison: an angle times a length is a length, so the rotation's
        // contribution over the body is |w|·extent and can be weighed against |t|.
        double turning = rotation.Length * extent;
        if (turning <= 1e-9 * (translation.Length + turning))
        {
            var direction = translation.Length > 0 ? translation / translation.Length : Vector3d.UnitX;
            return $"translation along {Format(direction)}";
        }

        var axis = rotation / rotation.Length;
        var point = centre + rotation.Cross(translation) / rotation.LengthSquared;
        double slide = translation.Dot(axis);
        string turn = $"rotation about the axis through {Format(point)} along {Format(axis)}";
        return Math.Abs(slide) > 1e-9 * (Math.Abs(slide) + turning)
            ? turn + " combined with a slide along it"
            : turn;
    }

    private static string Format(Vector3d v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";
}

/// <summary>
/// Eigen-decomposition of a small dense symmetric matrix by cyclic Jacobi rotations,
/// returning eigenvalues and the eigenvectors as columns of an n-by-n row-major matrix.
///
/// <para>Unconditionally convergent, deterministic, and used here for two jobs that both
/// need the VECTORS: the rigid-body null space (<see cref="RigidBodyModes"/>), where the
/// columns whose eigenvalue is negligible are the surviving motions a rank count alone
/// cannot name; and the Rayleigh-Ritz step of <see cref="LanczosEigen"/>, whose tridiagonal
/// projection is at most a few hundred wide.</para>
///
/// <para>Core's <c>SymmetricEigen3</c> does the same job for 3x3; this is the same algorithm
/// at small n and deliberately local rather than a generalization of a type whose whole API
/// is three-dimensional.</para>
/// </summary>
internal static class SmallSymmetricEigen
{
    /// <summary>Eigenvalues and eigenvectors (as columns) of a row-major symmetric n-by-n
    /// matrix. The input is not modified.</summary>
    public static (double[] Values, double[] Vectors) Solve(double[] a, int n)
    {
        var m = (double[])a.Clone();
        var v = new double[n * n];
        for (int i = 0; i < n; i++)
            v[i * n + i] = 1.0;

        for (int sweep = 0; sweep < 60; sweep++)
        {
            // Converged when the off-diagonal mass is negligible against the diagonal's
            // — a RELATIVE test, the form SymmetricEigen3 uses. An exact-zero test would
            // essentially never fire (round-off keeps a sum of squares positive), leaving
            // the sweep cap as the real termination rule, which the loop does not say.
            double off = 0, diagonal = 0;
            for (int p = 0; p < n; p++)
            {
                diagonal += m[p * n + p] * m[p * n + p];
                for (int q = p + 1; q < n; q++)
                    off += m[p * n + q] * m[p * n + q];
            }
            if (off <= 1e-30 * diagonal)
                break;

            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = m[p * n + q];
                    // Exact-zero semantic test: an entry that is already zero needs no
                    // rotation to annihilate it.
                    if (apq == 0)
                        continue;
                    double theta = (m[q * n + q] - m[p * n + p]) / (2 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                        t = 1;
                    double cos = 1.0 / Math.Sqrt(t * t + 1);
                    double sin = t * cos;

                    for (int k = 0; k < n; k++)
                    {
                        double akp = m[k * n + p], akq = m[k * n + q];
                        m[k * n + p] = cos * akp - sin * akq;
                        m[k * n + q] = sin * akp + cos * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = m[p * n + k], aqk = m[q * n + k];
                        m[p * n + k] = cos * apk - sin * aqk;
                        m[q * n + k] = sin * apk + cos * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k * n + p], vkq = v[k * n + q];
                        v[k * n + p] = cos * vkp - sin * vkq;
                        v[k * n + q] = sin * vkp + cos * vkq;
                    }
                }
            }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++)
            values[i] = m[i * n + i];
        return (values, v);
    }
}
