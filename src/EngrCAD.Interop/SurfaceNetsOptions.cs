namespace EngrCAD.Interop;

/// <summary>
/// How <see cref="SurfaceNets.Polygonize(EngrCAD.Implicit.Sdf, int, EngrCAD.Core.ProgressCancel?, SurfaceNetsOptions?)"/>
/// places its vertices and how coarse it is allowed to be. Every member has a default
/// that is a decision rather than a placeholder; see each one.
/// </summary>
public sealed record SurfaceNetsOptions
{
    /// <summary>The defaults — sharp features on, no simplification.</summary>
    public static SurfaceNetsOptions Default { get; } = new();

    /// <summary>
    /// Place each vertex at the minimiser of the quadratic error function of the field's
    /// own tangent planes at that vertex's crossings (dual contouring with Hermite data)
    /// rather than at the mean of the crossings.
    /// <para>
    /// <b>Default ON, deliberately, and this is the one option here that MOVES existing
    /// geometry.</b> The repo's usual precedent is that a new look ships opt-in with
    /// byte-identity as proof — but a chamfered box is not a look. Every crossing lies on
    /// the surface, so their mean lies strictly inside a convex corner: <c>Sdf.Box(10, 10,
    /// 10)</c> polygonized at resolution 32 put its nearest vertex 0.217 from the exact
    /// corner (0.50 of a cell) and no resolution removed it, because it is what the
    /// averaging rule computes rather than an error in computing it. The precedent that
    /// fits is <c>HoleFillOptions.Fallback</c>, which shipped as <c>None</c> and was
    /// changed to <c>Minimal</c> because that tier INVENTS NOTHING: this one invents
    /// nothing either — the planes are the field's own gradients, read where the surface
    /// already is.
    /// </para>
    /// <para>
    /// Turn it off to reproduce the pre-feature output bit for bit, which is what the
    /// golden fingerprints and the bit-identity tests do.
    /// </para>
    /// </summary>
    public bool SharpFeatures { get; init; } = true;

    /// <summary>
    /// The smallest deviation from flat, in degrees, that counts as a feature: a crease
    /// shallower than this is not resolved and its vertex keeps the incumbent averaged
    /// position in that direction.
    /// <para>
    /// It is a threshold on the ANGLE rather than on a singular value because that is the
    /// quantity a model has: two unit normals separated by α make the singular-value ratio
    /// exactly <c>tan(α/2)</c>, so the number below is converted rather than guessed. The
    /// default 10° is well under any feature a grid can resolve and well over the
    /// per-cell normal variation of a smooth surface (a sphere of radius 5 at cell 0.2
    /// varies by 2.3° across a cell), so it separates the two populations rather than
    /// splitting either.
    /// </para>
    /// <para>
    /// The direction of the risk is worth stating: raising it declines to resolve genuine
    /// features and returns the incumbent chamfer, while lowering it inverts an
    /// ill-conditioned direction and sends the vertex a long way outside its own cell.
    /// The first failure is a worse mesh; the second is a broken one.
    /// </para>
    /// </summary>
    public double FeatureAngleDegrees { get; init; } = 10;

    /// <summary>
    /// How far outside its own cell a feature vertex may be placed, in cells. The clamp box
    /// is the cell grown by this much on every side; 0 is the strict cell and
    /// <see cref="double.PositiveInfinity"/> is no clamp at all.
    /// <para>
    /// <b>Both extremes were built and measured, and both are wrong.</b> A quadric
    /// minimiser can land outside its own cell, which is the classic route to
    /// self-intersecting dual contouring; clamping to the cell is the classic fix and its
    /// classic objection is that it defeats the feature on exactly the cells that needed
    /// it. The objection is REAL and the axis-aligned box hides it completely: a ROTATED
    /// box reads a worst vertex-off-surface of 0.141 / 0.109 / 0.048 at resolutions
    /// 32 / 48 / 96 under a strict cell clamp — a quarter of a cell, converging only
    /// linearly — against <b>4.5e-12 with no clamp at all</b>, because a cell that sees
    /// both faces of an edge need not contain the edge, so the minimiser on the edge LINE
    /// is legitimately just outside. Refusing it there chamfers precisely the feature the
    /// quadric found.
    /// </para>
    /// <para>
    /// And no clamp is unbounded: on a gyroid whose wall (0.2) is thinner than its cell,
    /// the free solve moves vertices <b>4.3 cells</b> out — past their neighbours'
    /// neighbours, so the quads around them stop being a discretization of anything.
    /// </para>
    /// <para>
    /// The default 1 is the bound the data itself supports rather than a compromise
    /// between the two numbers: a cell's crossings lie on its own cube edges and the
    /// tangent planes they define are a FIT to that neighbourhood, so a point one cell
    /// beyond is still inside the region the fit's data came from and a point three cells
    /// beyond is an extrapolation into cells that contributed nothing. Measured, it is
    /// exact on every box placement (4.5e-12, the free solve's own number) and bounds the
    /// gyroid.
    /// </para>
    /// </summary>
    public double ClampCells { get; init; } = 1;

    /// <summary>
    /// Adaptive output: when set, cells whose vertices carry the same surface to within
    /// this error are merged, so a flat region costs a few quads instead of one per cell.
    /// The value is a LENGTH — the root-mean-square distance a merged vertex is allowed to
    /// sit from the tangent planes its cluster swallowed — and null (the default) keeps
    /// the uniform grid exactly.
    /// <para>
    /// See <see cref="SurfaceNets"/>' remarks for why this is bottom-up (a collapse of
    /// cells already visited) rather than a top-down octree that stops subdividing where
    /// the field looks flat: the second cannot certify that it has not missed a feature,
    /// which is precisely the argument <see cref="SurfaceCull"/> is built on.
    /// </para>
    /// </summary>
    public double? SimplifyTolerance { get; init; }

    /// <summary>
    /// How many octree levels the adaptive pass may collapse. Each level doubles the
    /// coarsest possible cell, so the default 8 reaches a 256-cell cluster — past any
    /// resolution this polygonizer accepts, i.e. "as far as the tolerance allows".
    /// </summary>
    public int MaxSimplifyLevels { get; init; } = 8;

    /// <summary>
    /// The singular-value ratio <see cref="FeatureAngleDegrees"/> means, as
    /// <see cref="SurfaceQef.Solve"/> consumes it.
    /// </summary>
    internal double SingularRatio => Math.Tan(FeatureAngleDegrees * Math.PI / 360);
}
