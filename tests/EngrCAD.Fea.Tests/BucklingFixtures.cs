using EngrCAD.Core;

namespace EngrCAD.Fea.Tests;

/// <summary>Which end conditions a verification column carries.</summary>
internal enum ColumnEnds
{
    /// <summary>Both ends laterally held, both free to rotate. Effective length
    /// <c>1.0·L</c>.</summary>
    PinnedPinned,

    /// <summary>One end built in, the other entirely free — the cantilever column.
    /// Effective length <c>2.0·L</c>.</summary>
    FixedFree,

    /// <summary>One end built in, the other laterally held and free to rotate. Effective
    /// length <c>0.6992·L</c>, the root of <c>tan(x) = x</c>.</summary>
    FixedPinned,

    /// <summary>Both ends built in. Effective length <c>0.5·L</c>.</summary>
    FixedFixed,
}

/// <summary>
/// Prismatic columns for Euler buckling verification, and the closed forms they are checked
/// against.
///
/// <para><b>Poisson's ratio is exactly zero, and that is what makes these fixtures measure
/// the solver rather than the boundary conditions.</b> Every end condition below holds
/// <c>u_y</c> and <c>u_z</c> over a whole end face — that is what "laterally supported"
/// means in a solid model — and at any other Poisson's ratio those restraints would fight
/// the lateral contraction the axial load produces, putting a non-uniform disturbance into
/// the prestress field exactly where the buckling mode's curvature is largest. With
/// <c>nu = 0</c> a uniaxial compression produces NO lateral strain, so the restraint is
/// satisfied identically, the FE stress field is exactly <c>-P/A</c> everywhere, and the
/// geometric stiffness it produces is exact rather than nearly so. The axial-bar fixture in
/// <see cref="ModalFixtures"/> is the same trick for the same reason.</para>
///
/// <para><b>Because the prestress is exact, the load factors are Rayleigh quotients over a
/// subspace and must converge from ABOVE.</b> A coarser mesh is a smaller trial space, so
/// its minimum of <c>phi'K phi / (-phi'Kg phi)</c> can only be larger — a theorem about the
/// method, not a property of these fixtures, and the right shape of assertion for a sequence
/// converging onto something that is not the analytic value being quoted. (What it converges
/// onto is the three-dimensional answer, which sits BELOW Euler by the shear correction
/// quoted in <see cref="EngesserRatio"/>.)</para>
///
/// <para><b>How each end condition is spelled in a solid model, and why.</b> A face whose
/// <c>u_x</c> is held over the WHOLE face cannot rotate — pure bending has
/// <c>u_x = -z·w'(x)</c>, which is non-zero across the section wherever the slope is — so
/// holding <c>u_x</c> on a face is a CLAMP and leaving it free is a PIN. That single fact
/// decides all four:</para>
/// <list type="bullet">
/// <item>fixed-free: one face fully fixed, a traction on the other.</item>
/// <item>fixed-pinned: one face fully fixed, the other holding <c>u_y</c>/<c>u_z</c> only,
/// with the traction on it.</item>
/// <item>fixed-fixed: one face fully fixed, the other given an enforced axial displacement
/// over the whole face — which is simultaneously the load and the "stays plane and
/// perpendicular" condition a clamp needs, and cannot be expressed as a traction without a
/// multi-point constraint.</item>
/// <item>pinned-pinned: both faces holding <c>u_y</c>/<c>u_z</c> only, equal and opposite
/// tractions on them, and <c>u_x</c> held on the MID-SPAN cross-section. That last is a
/// constraint the wanted mode satisfies IDENTICALLY — the first buckling mode is a symmetric
/// half sine, so its slope and therefore its <c>u_x</c> are exactly zero at mid-span — which
/// is the same device the simply-supported modal fixture uses, and for the same reason: a
/// single-node axial pin would be a static idiom that creates a genuine spurious mode. It is
/// equally invisible to the PRESTRESS, since the two halves compress towards mid-span and
/// the constraint carries no net reaction.</item>
/// </list>
/// </summary>
internal static class BucklingFixtures
{
    /// <summary>The reference compressive load every traction-loaded column carries, in
    /// newtons. Arbitrary — the load factor is a ratio — but fixed so the reported numbers
    /// are comparable.</summary>
    public const double ReferenceLoad = 1000.0;

    /// <summary>Steel with Poisson's ratio exactly zero (see the class remarks).</summary>
    public static Material Material => ModalFixtures.UncoupledSteel;

    /// <summary>Second moment of area of the square section.</summary>
    public static double SecondMoment(double side) => side * side * side * side / 12.0;

    /// <summary>The effective-length factor <c>K</c> in <c>P = pi²EI/(K·L)²</c>.</summary>
    public static double EffectiveLengthFactor(ColumnEnds ends) => ends switch
    {
        ColumnEnds.PinnedPinned => 1.0,
        ColumnEnds.FixedFree => 2.0,
        // The root of tan(x) = x in (pi, 3pi/2) is 4.493409458, and the effective length is
        // pi/4.493409458 = 0.699155. Quoted to the digits it is known to rather than as the
        // rounded 0.7 every table prints, because a 0.12% rounding would be a tenth of the
        // discretization error being measured against it.
        ColumnEnds.FixedPinned => Math.PI / 4.4934094579090642,
        ColumnEnds.FixedFixed => 0.5,
        _ => throw new ArgumentOutOfRangeException(nameof(ends)),
    };

    /// <summary>
    /// The Euler critical load <c>pi²EI/(K·L)²</c> — the SLENDER-column limit, with no shear
    /// deformation in it.
    /// </summary>
    public static double EulerLoad(ColumnEnds ends, double length, double side, Material material)
    {
        double effective = EffectiveLengthFactor(ends) * length;
        return Math.PI * Math.PI * material.YoungsModulus * SecondMoment(side)
            / (effective * effective);
    }

    /// <summary>
    /// Engesser's shear correction, <c>P_cr / P_Euler = 1 / (1 + P_Euler/(k·A·G))</c> with
    /// <c>k = 5/6</c> for a rectangle — the ratio by which a real three-dimensional column is
    /// SOFTER than the Euler value, because Euler's derivation has no shear deformation in
    /// it.
    ///
    /// <para>This is the buckling twin of the Timoshenko correction the modal beam fixtures
    /// quote, and it is here for the same reason: the finite element model converges onto the
    /// three-dimensional answer, not onto the formula, so a refinement study measured against
    /// the formula alone would stall at the modelling gap and report a number about beam
    /// theory. The gap is small but it is not noise — 0.49% for the pinned-pinned column at
    /// these proportions and 1.9% for the fixed-fixed one, whose Euler load is four times
    /// larger.</para>
    /// </summary>
    public static double EngesserRatio(
        ColumnEnds ends, double length, double side, Material material,
        double shearFactor = 5.0 / 6.0)
    {
        double euler = EulerLoad(ends, length, side, material);
        double shearStiffness = shearFactor * side * side * material.ShearModulus;
        return 1.0 / (1.0 + euler / shearStiffness);
    }

    /// <summary>The shear-corrected critical load — what a slender three-dimensional column
    /// should actually measure.</summary>
    public static double ShearCorrectedLoad(
        ColumnEnds ends, double length, double side, Material material) =>
        EulerLoad(ends, length, side, material)
        * EngesserRatio(ends, length, side, material);

    /// <summary>
    /// Builds a column along X of square section <paramref name="side"/>, loaded in
    /// compression under the named end conditions, and returns the model together with the
    /// reference axial load its stress field carries.
    /// </summary>
    /// <param name="ends">Which end conditions.</param>
    /// <param name="length">Column length along X.</param>
    /// <param name="side">Square section side.</param>
    /// <param name="nx">Elements along the length. Must be EVEN for
    /// <see cref="ColumnEnds.PinnedPinned"/>, which needs a node plane at mid-span.</param>
    /// <param name="across">Elements across each transverse direction.</param>
    /// <param name="order">Element order.</param>
    public static (StructuralModel Model, double ReferenceLoad) Column(
        ColumnEnds ends, double length, double side, int nx, int across, ElementOrder order)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, side, side), nx, across, across);
        var mesh = order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Material);
        double load = ReferenceLoad;

        switch (ends)
        {
            case ColumnEnds.PinnedPinned:
                if (nx % 2 != 0)
                    throw new ArgumentException(
                        "A pinned-pinned column needs a node plane at mid-span to hold u_x on, "
                        + $"so nx must be even; {nx} was given.", nameof(nx));
                model.Fix(Facets.Tag(StructuredTetMesh.XMin), Dof.Y | Dof.Z);
                model.Fix(Facets.Tag(StructuredTetMesh.XMax), Dof.Y | Dof.Z);
                FixMidSpanAxially(model, length);
                model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(-load, 0, 0));
                model.Force(Facets.Tag(StructuredTetMesh.XMin), new Vector3d(load, 0, 0));
                break;

            case ColumnEnds.FixedFree:
                model.Fix(Facets.Tag(StructuredTetMesh.XMin));
                model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(-load, 0, 0));
                break;

            case ColumnEnds.FixedPinned:
                model.Fix(Facets.Tag(StructuredTetMesh.XMin));
                model.Fix(Facets.Tag(StructuredTetMesh.XMax), Dof.Y | Dof.Z);
                model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(-load, 0, 0));
                break;

            case ColumnEnds.FixedFixed:
                // An enforced end displacement rather than a traction: it is the only way to
                // say "this face stays plane, perpendicular and laterally fixed while moving
                // axially" without a multi-point constraint, and that IS a clamp. With nu = 0
                // the resulting stress is exactly E·delta/L, so the reference load below is
                // an analytic value rather than a measured reaction - and the test asserts
                // the reaction against it.
                double shortening = length * 1e-4;
                load = Material.YoungsModulus * side * side * shortening / length;
                model.Fix(Facets.Tag(StructuredTetMesh.XMin));
                model.Prescribe(
                    Facets.Tag(StructuredTetMesh.XMax), new Vector3d(-shortening, 0, 0));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(ends));
        }

        return (model, load);
    }

    /// <summary>
    /// Holds <c>u_x</c> on every node of the mid-span cross-section. Per NODE rather than
    /// through a facet selector because mid-span is an INTERIOR plane and
    /// <see cref="Facets"/> selects boundary facets only — the same reason
    /// <see cref="ModalFixtures.AxialBar"/> restrains its transverse degrees of freedom node
    /// by node.
    /// </summary>
    private static void FixMidSpanAxially(StructuralModel model, double length)
    {
        var mesh = model.Mesh;
        double mid = 0.5 * length;
        // The 1e-9 absolute weld tier, expressed relatively: the structured mesh's node
        // coordinates are computed as origin + i·size/n, so a mid-span node's x differs from
        // L/2 by a few ulps of L rather than by anything geometric.
        double tolerance = 1e-9 * length;
        int held = 0;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            if (Math.Abs(mesh.Position(v).X - mid) > tolerance)
                continue;
            model.FixNode(v, Dof.X);
            held++;
        }
        if (held == 0)
            throw new InvalidOperationException(
                $"No node lies on the mid-span plane x = {mid}, so the pinned-pinned column "
                + "has nothing holding it axially.");
    }
}
