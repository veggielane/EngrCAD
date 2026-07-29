using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// Element-level checks and measurements shared by every solver over an
/// <see cref="AnalysisMesh"/>.
///
/// <para><b>Shared because the guard's whole point is that it asks the ASSEMBLY's own
/// arithmetic.</b> The structural solver learned this the expensive way: a first version
/// tested the corner triple product while assembly integrated the isoparametric Jacobian
/// — the same mathematical quantity by different arithmetic, disagreeing in the last bits,
/// so elements passed the guard and were then integrated as exactly zero. Conduction
/// integrates the SAME Jacobian at the SAME quadrature points, so a second copy of this
/// test would be the same defect waiting to happen a third time.</para>
/// </summary>
internal static class FeaGuards
{
    /// <summary>
    /// A normalised shape measure: the element's volume over the cube of its longest
    /// edge. Scale-free by construction, capped at 1/(6·sqrt2) = 0.1179 for a regular
    /// tetrahedron, and zero for a flat one. Not a decision anywhere in a solver — only a
    /// MEASUREMENT, reported when a system will not factor.
    /// </summary>
    public static double ShapeMeasure(AnalysisMesh mesh, int element)
    {
        var e = mesh.Element(element);
        double longest = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = i + 1; j < 4; j++)
                longest = Math.Max(longest, mesh.Position(e[i]).DistanceTo(mesh.Position(e[j])));
        }
        // Exact-zero division guard (the scale-free tier): a tetrahedron with no longest
        // edge has all four vertices coincident and no shape to measure.
        if (longest == 0)
            return 0;
        return mesh.ElementVolume(element) / (longest * longest * longest);
    }

    /// <summary>
    /// A sentence about the mesh's element shapes, for a refusal message: how many
    /// elements are flat enough to cost a factorization its definiteness, and the worst.
    /// </summary>
    public static string DescribeElementShape(AnalysisMesh mesh)
    {
        int slivers = 0;
        double worst = double.MaxValue;
        int worstElement = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double measure = ShapeMeasure(mesh, e);
            if (measure < 1e-4)
                slivers++;
            if (measure < worst)
            {
                worst = measure;
                worstElement = e;
            }
        }
        return
            $" Element shape: {slivers:N0} of {mesh.ElementCount:N0} elements measure below"
            + $" 1e-4 on volume/longest-edge-cubed (0.1179 is regular); the worst is element"
            + $" {worstElement} at {worst:E3}."
            + (slivers > 0
                ? " Slivers that flat make the assembled matrix numerically singular; refine the"
                  + " mesh or mesh a less elongated body."
                : "");
    }

    /// <summary>
    /// Refuses a mesh carrying an element whose Jacobian is non-positive in floating
    /// point, naming it.
    ///
    /// <para>The tetrahedral mesher guarantees a strictly positive volume by the EXACT
    /// predicate, which is the right guarantee for topology and says nothing about the
    /// double-precision Jacobian a solver integrates with. A sliver flat enough for the
    /// two to disagree contributes a matrix block of the wrong SIGN — not an inaccuracy
    /// but a negative-definite block that destroys the whole system's definiteness — so it
    /// is refused rather than absorbed. Measured on a 100 x 10 x 10 beam at a 5.0 size
    /// target: two such elements out of 31 214.</para>
    ///
    /// <para><b>The test is the assembly's OWN determinant, at the assembly's own
    /// quadrature points</b> (see the class remarks).</para>
    /// </summary>
    /// <param name="mesh">The analysis mesh.</param>
    /// <param name="rule">The rule the assembly will integrate with — the same one, not an
    /// equivalent one.</param>
    /// <param name="matrixName">What the element would contribute to ("stiffness",
    /// "conductivity"), so the message says which solve refused.</param>
    public static void RequireUsableElements(
        AnalysisMesh mesh, in TetQuadrature rule, string matrixName)
    {
        var bad = new List<string>();
        int count = 0;
        int perElement = mesh.NodesPerElement;
        Span<Vector3d> positions = stackalloc Vector3d[10];
        Span<Vector3d> gradient = stackalloc Vector3d[10];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            for (int i = 0; i < perElement; i++)
                positions[i] = mesh.Position(nodes[i]);

            double worst = double.MaxValue;
            for (int q = 0; q < rule.Count; q++)
            {
                var (r, s, t) = rule.Point(q);
                TetElement.ShapeGradients(
                    mesh.Order, positions[..perElement], r, s, t, gradient, out double detJ);
                worst = Math.Min(worst, detJ);
            }
            if (worst > 0)
                continue;

            count++;
            if (bad.Count < 5)
                bad.Add($"element {e} (Jacobian {worst:E3}, corner volume "
                    + $"{mesh.ElementVolume(e):E3}) at {mesh.Position(nodes[0])}");
        }
        if (count == 0)
            return;

        throw new FeaException(
            $"{count:N0} of {mesh.ElementCount:N0} elements have a non-positive Jacobian in double "
            + $"precision, so their {matrixName} would have the wrong sign and the assembled system "
            + $"would not be positive definite. {string.Join("; ", bad)}"
            + (count > bad.Count ? " (and more)" : "") + ". "
            + "The tetrahedral mesher guarantees positive orientation by the EXACT predicate, "
            + "which a sliver can satisfy while its floating-point volume underflows to zero or "
            + "below; this is the sliver-removal gap named in the mesher's README. Refine the "
            + "mesh, or mesh a less elongated body.");
    }
}
