using EngrCAD.Core;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Beam and bar fixtures for modal verification, and the closed-form frequencies they are
/// checked against.
///
/// <para><b>Why the bar is the fixture the convergence ORDER is measured on.</b> Every beam
/// comparison here carries a modelling difference as well as a discretization error: a 3D
/// solid includes shear deformation and rotary inertia, which Euler-Bernoulli theory does
/// not, so refining the mesh converges onto the 3D answer and NOT onto the formula. That
/// difference is a few percent at the first mode and tens of percent by the third, and it
/// does not shrink with h — so a "convergence order" measured against a beam formula would
/// stall at the modelling gap and report a number about beam theory. The axial bar below is
/// built so that the 3D and 1D problems are the SAME problem, exactly, and it is where the
/// order is measured. It is the same lesson the static solver's clamped-end singularity
/// taught, in a different disguise.</para>
/// </summary>
internal static class ModalFixtures
{
    /// <summary>The nominal steel of <c>Materials.Steel</c>, which every fixture here uses
    /// unless it needs a zero Poisson's ratio.</summary>
    public static Material Steel => Materials.Steel;

    /// <summary>
    /// Steel with <b>Poisson's ratio exactly zero</b> — the material that makes the axial
    /// bar exact rather than nearly so.
    ///
    /// <para>With <c>nu = 0</c> Lame's first parameter is zero, so the axial and transverse
    /// directions decouple completely: an axial stretch produces no lateral contraction and
    /// the uniaxial modulus IS <c>E</c>. Combined with <see cref="AxialBar"/>'s transverse
    /// restraints, the 3D finite element problem reduces EXACTLY to the one-dimensional bar
    /// whose frequencies are <c>n/(2L)·sqrt(E/rho)</c>. At any other Poisson's ratio the
    /// restrained bar would carry the CONSTRAINED modulus <c>lambda + 2mu</c> instead, which
    /// for steel is 40% higher — the fixture would then be measuring a different formula and
    /// looking like a solver defect.</para>
    /// </summary>
    public static Material UncoupledSteel { get; } =
        new("steel, nu = 0", Materials.Steel.YoungsModulus, 0.0, Materials.Steel.Density);

    /// <summary>The one-dimensional wave speed <c>sqrt(E/rho)</c>.</summary>
    public static double WaveSpeed(Material material) =>
        Math.Sqrt(material.YoungsModulus / material.Density);

    /// <summary>
    /// A prismatic bar along X, <b>restrained transversely at every node</b>, so the only
    /// motion left is axial and there are no bending or torsional modes to hide the axial
    /// ones among. Axially it is FREE-FREE, so it carries exactly one rigid-body mode (the
    /// translation along X) and its elastic frequencies are <c>n/(2L)·sqrt(E/rho)</c>.
    ///
    /// <para>Restraining every node rather than the end faces is deliberate: a quadratic
    /// mesh has mid-edge nodes on interior cube diagonals that belong to no boundary facet,
    /// so a facet selector would leave them free and the "1D" bar would quietly regain
    /// transverse degrees of freedom.</para>
    /// </summary>
    public static StructuralModel AxialBar(
        double length, double side, int nx, ElementOrder order, Material? material = null)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, side, side), nx, 1, 1);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, material ?? UncoupledSteel);
        for (int v = 0; v < mesh.NodeCount; v++)
            model.FixNode(v, Dof.Y | Dof.Z);
        return model;
    }

    /// <summary>The exact axial frequency of a free-free (or fixed-fixed) bar,
    /// <c>n/(2L)·sqrt(E/rho)</c>.</summary>
    public static double AxialFrequency(int n, double length, Material material) =>
        n / (2.0 * length) * WaveSpeed(material);

    /// <summary>A prismatic beam along X of the given cross-section, meshed as a
    /// structured grid.</summary>
    public static AnalysisMesh Beam(
        double length, double width, double depth, int nx, int ny, int nz, ElementOrder order)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, width, depth), nx, ny, nz);
        return order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
    }

    /// <summary>Second moment of area of a rectangle about the axis perpendicular to
    /// <paramref name="depth"/>.</summary>
    public static double SecondMoment(double width, double depth) => width * depth * depth * depth / 12.0;

    /// <summary>
    /// The Euler-Bernoulli natural frequency of a uniform beam,
    /// <c>f = (beta·L)²/(2·pi·L²)·sqrt(EI/(rho·A))</c>.
    /// </summary>
    /// <param name="betaL">The mode's <c>beta·L</c>: 1.875104 / 4.694091 / 7.854757 for a
    /// cantilever, <c>n·pi</c> for a simply-supported beam, 4.730041 / 7.853205 / 10.995608
    /// for a free-free one.</param>
    public static double BeamFrequency(
        double betaL, double length, double area, double secondMoment, Material material) =>
        betaL * betaL / (2.0 * Math.PI * length * length)
        * Math.Sqrt(material.YoungsModulus * secondMoment / (material.Density * area));

    /// <summary>A cantilever's first three bending <c>beta·L</c> values.</summary>
    public static readonly double[] CantileverBetaL = [1.8751041, 4.6940911, 7.8547574];

    /// <summary>A free-free beam's first three ELASTIC bending <c>beta·L</c> values (the six
    /// rigid-body modes come first and are not counted here).</summary>
    public static readonly double[] FreeFreeBetaL = [4.7300408, 7.8532046, 10.9956078];

    /// <summary>
    /// The Timoshenko correction factor for a SIMPLY-SUPPORTED beam's n-th mode: the ratio
    /// of the shear-and-rotary-inertia frequency to the Euler-Bernoulli one, to first order.
    ///
    /// <para>Quoted only for the simply-supported case, where the mode shape is a pure sine
    /// and the correction has a closed form. The cantilever's higher modes have no such
    /// tidy expression, so this file does not pretend to one — their deviation from
    /// Euler-Bernoulli is REPORTED as measured rather than compared against a formula
    /// derived for other boundary conditions.</para>
    /// </summary>
    /// <param name="betaL">The mode's <c>beta·L</c> (<c>n·pi</c>).</param>
    /// <param name="radiusOfGyration">
    /// <c>sqrt(I/A)</c> for the bending direction.</param>
    /// <param name="length">Beam length.</param>
    /// <param name="material">The material.</param>
    /// <param name="shearFactor">Timoshenko's shear coefficient k, 5/6 for a
    /// rectangle.</param>
    public static double TimoshenkoRatio(
        double betaL, double radiusOfGyration, double length, Material material,
        double shearFactor = 5.0 / 6.0)
    {
        double slenderness = radiusOfGyration / length;
        double correction = 1.0
            + betaL * betaL * slenderness * slenderness
              * (1.0 + material.YoungsModulus / (shearFactor * material.ShearModulus));
        return 1.0 / Math.Sqrt(correction);
    }

    /// <summary>The measured convergence order between two errors at a 2:1 refinement.</summary>
    public static double Order(double coarseError, double fineError) =>
        Math.Log2(Math.Abs(coarseError) / Math.Abs(fineError));
}
