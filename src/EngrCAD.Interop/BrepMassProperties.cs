using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>Tessellation settings for <see cref="BrepMassProperties"/>.</summary>
public sealed record BrepMassPropertyOptions
{
    /// <summary>Segments per full circle (the same knob <c>BRepTessellator</c> takes).
    /// Defaults to 64 — twice the display default, because a mass figure is read as a
    /// number rather than looked at.</summary>
    public int SegmentsPerCircle { get; init; } = 64;

    /// <summary>Samples along non-circular curves.</summary>
    public int CurveSamples { get; init; } = 32;

    /// <summary>
    /// Integrate twice (at this density and at double it) and Richardson-extrapolate away
    /// the leading O(h²) term. <b>On by default</b>, because it is what makes the answer a
    /// mass-properties number rather than a display-mesh estimate: measured relative
    /// volume error at the default 64 segments/circle goes from 1.6e-3 to 1.9e-7 for a
    /// cylinder, 2.2e-3 to 4.8e-7 for a sphere, 2.0e-3 to 3.7e-7 for a torus, and — for a
    /// boolean result with trimmed faces — 1.1e-4 to 1.4e-8 for a drilled plate. The cost
    /// is a second tessellation at double density.
    /// <para>Exact answers stay exact: a planar-faced solid tessellates identically at both
    /// densities, so (4P₂ − P₁)/3 returns P unchanged.</para>
    /// <para>Turn it off for geometry whose tessellation error is not smooth in h — a face
    /// that takes the trimmed path at one density and the grid fallback at the other jumps
    /// rather than converges, and extrapolation amplifies a jump by 4/3 instead of
    /// cancelling it.</para>
    /// </summary>
    public bool Extrapolate { get; init; } = true;

    public static readonly BrepMassPropertyOptions Default = new();
}

/// <summary>
/// Mass properties of a <see cref="BrepSolid"/> — volume, area, centre of mass and
/// inertia (OCCT's <c>BRepGProp</c>).
/// </summary>
/// <remarks>
/// <para><b>The route is tessellate-then-sum, deliberately, and it is not exact for curved
/// faces.</b> The alternative — Gauss quadrature over each face's exact surface, which is
/// what OCCT does — needs the trimmed parameter domain scanned against the trimming curves
/// (OCCT's <c>GProp_Domain</c>), and this kernel's faces are trimmed by pulled-back
/// polylines whose parameter-space boundary is itself only approximate for
/// marching-tracer edges. Quadrature over an approximate domain is not exact either; it
/// would merely hide its error behind a more impressive-looking integral. Tessellating
/// keeps the error in one place, makes it measurable, and lets the caller buy accuracy
/// with <see cref="BrepMassPropertyOptions.SegmentsPerCircle"/>.</para>
/// <para>What that costs, measured (see <c>BrepMassPropertiesTests</c>):</para>
/// <list type="bullet">
/// <item><description><b>Planar-faced solids are exact</b> — a box, a prism, an extruded
/// sketch, a drilled plate's flats. Triangulating a planar polygon covers it exactly, so
/// the divergence-theorem sum is the closed form to round-off. This is not a tolerance
/// claim; it is an identity.</description></item>
/// <item><description><b>Curved faces converge as O(h²)</b>, always under-estimating (the
/// tessellation is inscribed) — a plain sum at n segments/circle is low by ≈ 2π²/3n²
/// relative, measured 1.6e-3 at n = 64 and 4.0e-4 at n = 128 for a cylinder. Because that
/// error is a clean O(h²) series, <see cref="BrepMassPropertyOptions.Extrapolate"/>
/// (<b>on by default</b>) integrates at n and 2n and cancels it, reaching ~2e-7 relative
/// at n = 64 and ~1e-8 at n = 128 — including on boolean results with trimmed
/// faces.</description></item>
/// </list>
/// <para>So the honest one-line summary: <b>exact for planar-faced solids, ~1e-7 relative
/// for curved ones at the default settings</b>, with a knob that buys more.</para>
/// </remarks>
public static class BrepMassProperties
{
    /// <inheritdoc cref="BrepMassProperties"/>
    /// <param name="solid">A closed, valid solid.</param>
    /// <param name="density">Mass per unit volume in the caller's units; 1 makes mass equal volume.</param>
    /// <param name="options">Tessellation accuracy; null uses <see cref="BrepMassPropertyOptions.Default"/>.</param>
    public static MassProperties Compute(
        BrepSolid solid, double density = 1.0, BrepMassPropertyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(solid);
        var o = options ?? BrepMassPropertyOptions.Default;

        var coarse = FromTessellation(solid, density, o.SegmentsPerCircle, o.CurveSamples);
        if (!o.Extrapolate)
            return coarse;

        var fine = FromTessellation(solid, density, o.SegmentsPerCircle * 2, o.CurveSamples * 2);
        return Extrapolated(coarse, fine);
    }

    private static MassProperties FromTessellation(
        BrepSolid solid, double density, int segmentsPerCircle, int curveSamples)
    {
        var mesh = BRepTessellator.Tessellate(solid, segmentsPerCircle, curveSamples);
        // requireClosed: false — a tessellation may carry T-junction seams and still be
        // geometrically watertight, exactly as HalfEdgeMesh.SignedVolume allows. An
        // actually-open tessellation shows up as a wrong volume, which is what the
        // boolean layer's own verification is for.
        return MeshMassProperties.Compute(mesh, density, requireClosed: false);
    }

    /// <summary>
    /// Richardson extrapolation of two integrations whose step halved: P ≈ (4·P₂ − P₁)/3,
    /// applied to the raw integrals (volume, area, first moment, second moment about a
    /// common reference) rather than to the derived centroid, so the identities between
    /// them survive.
    /// </summary>
    private static MassProperties Extrapolated(in MassProperties coarse, in MassProperties fine)
    {
        const double w = 4.0 / 3.0, wc = 1.0 / 3.0;
        double volume = w * fine.Volume - wc * coarse.Volume;
        double area = w * fine.SurfaceArea - wc * coarse.SurfaceArea;

        // A common reference near the body keeps the moment shifts well conditioned; the
        // coarse centroid is by definition inside it.
        var reference = coarse.Centroid;
        var firstMoment =
            w * (fine.Volume * (fine.Centroid - reference)) - wc * (coarse.Volume * (coarse.Centroid - reference));

        var coarseAboutReference =
            coarse.SecondMoment + coarse.Volume * SymmetricTensor3.OuterProduct(coarse.Centroid - reference);
        var fineAboutReference =
            fine.SecondMoment + fine.Volume * SymmetricTensor3.OuterProduct(fine.Centroid - reference);
        var aboutReference = w * fineAboutReference - wc * coarseAboutReference;

        // Exact-zero guard: an empty body has no centroid to place (division, not tolerance).
        if (volume == 0)
            return new MassProperties(0, area, coarse.Density, reference, SymmetricTensor3.Zero);

        var relativeCentroid = firstMoment / volume;
        return new MassProperties(
            volume, area, coarse.Density,
            reference + relativeCentroid,
            aboutReference - volume * SymmetricTensor3.OuterProduct(relativeCentroid));
    }

    /// <inheritdoc cref="Compute(BrepSolid, double, BrepMassPropertyOptions?)"/>
    public static MassProperties MassProperties(
        this BrepSolid solid, double density = 1.0, BrepMassPropertyOptions? options = null) =>
        Compute(solid, density, options);
}
