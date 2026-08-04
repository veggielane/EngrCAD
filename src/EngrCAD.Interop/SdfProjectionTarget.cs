using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// A <see cref="IProjectionTarget"/> backed by a signed distance field: the remesher's
/// quality-control pass for anything that came out of the implicit engine
/// (<see cref="SurfaceNets"/> output above all), and the way to remesh a mesh against a
/// <see cref="MeshSdf"/> of itself rather than against its own triangles.
/// <para>
/// One step is the Newton step along the gradient, <c>p' = p - d(p)·n(p)</c> with
/// <c>n = grad d / |grad d|</c>. For an <b>exact</b> distance field with an exact gradient
/// that is not an approximation at all: the gradient at <c>p</c> points straight away from
/// the closest surface point and <c>|p - c| = |d(p)|</c>, so one step lands exactly on
/// <c>c</c>. Two things spoil that in practice, and both are why the step is iterated:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>The gradient is a central difference</b>, so it carries O(h²·curvature) angular
///     error; the resulting positional error is O(|d|·h²·curvature), i.e. it vanishes as the
///     point approaches the surface. <see cref="GradientStep"/> is relative to the field's
///     own bounds, never an absolute constant.
///   </description></item>
///   <item><description>
///     <b>Most composed fields are not exact distances.</b> A CSG difference, a smooth blend
///     and a <see cref="MeshSdf"/> of a coarse mesh are all correct-sign <i>lower bounds</i>
///     with <c>|grad d| ≤ 1</c>, so a single step under-shoots by the factor the field is
///     short by, and the iteration converges linearly at that rate.
///   </description></item>
/// </list>
/// <para>
/// <b>What is guaranteed is one-sidedness, and that much is a theorem rather than a hope.</b>
/// Every field this engine builds is 1-Lipschitz and a lower bound on the true distance, so
/// the surface is at least <c>|d(p)|</c> away from <c>p</c> in <i>every</i> direction: a step
/// of exactly that length lands on the same side of the surface or exactly on it, and
/// <b>can never overshoot</b>, however wrong the gradient direction is. That is why no step
/// limiting or damping appears here — the field itself supplies the trust region. Where the
/// field is additionally smooth and locally exact, each step multiplies the residual by
/// <c>(1 - |grad d|)</c>, so convergence is linear and one or two steps are plenty.
/// </para>
/// <para>
/// <b>What is NOT guaranteed is that <c>|d|</c> decreases — and the counter-example is a
/// plain CSG difference, so it is worth knowing.</b> A subtracted tool leaves the field
/// reading the distance to a <i>fictitious</i> face (the tool's own surface, in the region
/// the tool removed), which is a strict lower bound with a gradient that jumps at the branch
/// switch. A step can therefore land on the other branch with a larger value, and two
/// branches can trade the point back and forth without converging at all: measured on
/// <c>Box(2,2,2) - Sphere(1.2)</c>, a probe 0.14 above the removed cap oscillates between the
/// cap plane and the sphere, six steps leaving it exactly where it started, while the true
/// distance to the real rim is 0.45. The sign never flips, which is the property that keeps
/// this safe to use, but the point does not arrive anywhere.
/// </para>
/// <para>
/// This matters much less than it sounds for the job at hand: a remeshed vertex starts
/// <i>on</i> the surface and stays within a fraction of an edge length of it, where every one
/// of these fields is locally the exact distance to the nearest real face and the gradient is
/// the real normal. Fictitious faces live at a finite depth inside the removed material. So —
/// project points that are already near the surface (which is what
/// <see cref="Remesher"/> does), and do not use this as a general closest-point query.
/// </para>
/// <para>
/// Cost is <c>1 + 6</c> field evaluations per step (value plus the central-difference
/// gradient), so an expensive AST — a <see cref="MeshSdf"/> especially — is worth wrapping in
/// <c>Sdf.Sampled(...)</c> or <c>Sdf.NarrowBand(...)</c> before remeshing against it.
/// </para>
/// </summary>
public sealed class SdfProjectionTarget : IProjectionTarget
{
    /// <summary>
    /// Default number of Newton steps per projection. One step is exact for an exact field;
    /// two is the cheapest number that also converges the lower-bound fields (CSG
    /// differences, smooth blends), and the second step costs nothing once the first has
    /// brought the point to within a fraction of an edge length.
    /// </summary>
    public const int DefaultIterations = 2;

    /// <summary>
    /// Central-difference step as a fraction of the field's bounding-box diagonal.
    /// <para>
    /// Scale-free by construction — an absolute constant here would be an epsilon on a
    /// <i>length</i> in a model whose units are nobody's business (the ladder's rule). The
    /// value is the classic optimum for a central difference: truncation error grows as h²
    /// and round-off as εₘ/h, which balance at h ≈ εₘ^(1/3) ≈ 6e-6 of the scale over which
    /// the field varies.
    /// </para>
    /// </summary>
    public const double RelativeGradientStep = 5e-6;

    private readonly Sdf _field;
    private readonly double _lipschitz;

    /// <summary>Wraps <paramref name="field"/> as a projection target.</summary>
    /// <param name="field">The field whose zero level set is the surface to project onto.</param>
    /// <param name="iterations">Newton steps per projection (see the class remarks).</param>
    /// <param name="gradientStep">
    /// Central-difference step. Defaults to <see cref="RelativeGradientStep"/> times the
    /// field's bounding-box diagonal, falling back to <see cref="RelativeGradientStep"/>
    /// itself for unbounded fields (half-spaces, lattices), whose scale nothing can report.
    /// </param>
    public SdfProjectionTarget(Sdf field, int iterations = DefaultIterations, double? gradientStep = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(nameof(iterations), "A projection needs at least one step.");
        if (gradientStep is <= 0)
            throw new ArgumentOutOfRangeException(nameof(gradientStep), "The gradient step must be positive.");

        _field = field;
        Iterations = iterations;
        GradientStep = gradientStep ?? DefaultGradientStep(field);
        _lipschitz = StepScale(field);
    }

    /// <summary>
    /// What the one-sidedness argument above needs when the field is NOT 1-Lipschitz. The
    /// theorem is that the surface is at least <c>|d| / L</c> away in every direction, so a
    /// step of that length can never overshoot; L is exactly 1 for every exact field, and
    /// dividing by it is the identity, so every incumbent target steps bit-identically. A
    /// twisted or bent field reports a larger L and takes proportionally shorter steps —
    /// slower convergence, never a crossing. An unbounded field has no finite bound to read,
    /// so it keeps the incumbent step: the operators that can exceed 1 all require finite
    /// bounds to be constructed at all.
    /// </summary>
    private static double StepScale(Sdf field)
    {
        var bounds = field.Bounds;
        if (!Sdf.IsFinite(bounds) || bounds.IsEmpty)
            return 1;
        double bound = field.LipschitzBound(bounds);
        return double.IsFinite(bound) && bound > 1 ? bound : 1;
    }

    /// <summary>Newton steps taken per <see cref="Project"/> call.</summary>
    public int Iterations { get; }

    /// <summary>The central-difference step used for the gradient, in model units.</summary>
    public double GradientStep { get; }

    /// <inheritdoc/>
    public Vector3d Project(in Vector3d point)
    {
        var p = point;
        for (int i = 0; i < Iterations; i++)
        {
            if (!TryStep(p, out var next, out _))
                break;
            p = next;
        }
        return p;
    }

    /// <summary>
    /// Projects and reports the surface normal there — the field's own unit gradient, which
    /// the last Newton step computed anyway, so the orientation face-aligned reprojection
    /// needs is free. A point already exactly on the surface reports no normal (the
    /// interface's spelling for "unoriented"), since no step was taken to read one from.
    /// </summary>
    public Vector3d Project(in Vector3d point, out Vector3d normal)
    {
        var p = point;
        normal = Vector3d.Zero;
        for (int i = 0; i < Iterations; i++)
        {
            if (!TryStep(p, out var next, out var n))
                break;
            p = next;
            normal = n;
        }
        if (normal == Vector3d.Zero)
        {
            // No step was taken — the point was already exactly on the surface — so read the
            // gradient where it stands rather than reporting "unoriented", which would send a
            // face-aligned remesh down its fallback path for the best-placed triangles it has.
            var gradient = Gradient(p);
            double length = gradient.Length;
            if (length > 0 && double.IsFinite(length))
                normal = gradient / length;
        }
        // Otherwise the normal reported is the one at the last point stepped FROM. Re-reading
        // it at the projected point would cost six more evaluations to move it by
        // O(|d|·curvature), which is below the positional error the step itself leaves behind.
        return p;
    }

    /// <summary>
    /// One Newton step. Returns false — leaving the point where it is — when the field is
    /// exactly zero there (already on the surface) or its gradient is degenerate, which is
    /// what happens at a medial-axis point equidistant from two sheets. Both are semantic
    /// exact-zero tests, not tolerances: a vanishing gradient carries no direction at all,
    /// and any epsilon here would be an epsilon on a quantity with no model units.
    /// </summary>
    private bool TryStep(in Vector3d p, out Vector3d next, out Vector3d normal)
    {
        next = p;
        normal = Vector3d.Zero;
        double d = _field.Evaluate(p);
        if (d == 0)
            return false;

        var gradient = Gradient(p);
        double length = gradient.Length;
        if (length == 0 || !double.IsFinite(length))
            return false;

        normal = gradient / length;
        next = p - normal * (_lipschitz == 1 ? d : d / _lipschitz);
        return true;
    }

    /// <summary>Central-difference gradient, unnormalized: six evaluations.</summary>
    private Vector3d Gradient(in Vector3d p)
    {
        double h = GradientStep;
        return new Vector3d(
            _field.Evaluate(p + (h, 0, 0)) - _field.Evaluate(p - (h, 0, 0)),
            _field.Evaluate(p + (0, h, 0)) - _field.Evaluate(p - (0, h, 0)),
            _field.Evaluate(p + (0, 0, h)) - _field.Evaluate(p - (0, 0, h)));
    }

    private static double DefaultGradientStep(Sdf field)
    {
        var bounds = field.Bounds;
        if (!Sdf.IsFinite(bounds) || bounds.IsEmpty)
            return RelativeGradientStep;
        double diagonal = bounds.Size.Length;
        return diagonal > 0 ? RelativeGradientStep * diagonal : RelativeGradientStep;
    }
}
