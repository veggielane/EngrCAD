using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Interop;

/// <summary>
/// What a section through a FLUSH plane should answer. A plane containing a whole planar face
/// has no cross-section CURVE there — the intersection is an area — so there is no one right
/// region and the enum is the caller's statement of which it means.
/// </summary>
public enum FlushSection
{
    /// <summary>Refuse by name (the default, and every incumbent call).</summary>
    Refuse = 0,

    /// <summary>The limit approached from the side the plane's normal points AWAY from.</summary>
    Below,

    /// <summary>The limit approached from the side the plane's normal points TOWARD.</summary>
    Above,

    /// <summary>Both limits unioned — which IS the set-theoretic <c>solid ∩ plane</c>, so it is
    /// what OpenSCAD's <c>projection(cut = true)</c> means.</summary>
    Union,
}

/// <summary>The two limits of a flush section, each an ordinary EXACT transversal section of
/// its own nudged plane, plus the nudge that was spent.</summary>
public sealed record FlushLimits(
    IReadOnlyList<Region2d> Below,
    IReadOnlyList<Region2d> Above,
    double Nudge)
{
    /// <summary>The two limits unioned — the set-theoretic <c>solid ∩ plane</c>.</summary>
    public IReadOnlyList<Region2d> Union() =>
        Region2dBoolean.UnionAll([.. Below, .. Above]);
}

/// <summary>The curved twin of <see cref="FlushLimits"/>.</summary>
public sealed record CurvedFlushLimits(
    IReadOnlyList<CurvedRegion2d> Below,
    IReadOnlyList<CurvedRegion2d> Above,
    double Nudge)
{
    /// <inheritdoc cref="FlushLimits.Union"/>
    public IReadOnlyList<CurvedRegion2d> Union() =>
        CurvedRegion2dBoolean.UnionAll([.. Below, .. Above]);
}

/// <summary>Thrown when a flush section's limits cannot be read.</summary>
public sealed class FlushSectionException : Exception
{
    public FlushSectionException(string message) : base(message) { }
}


public static partial class PlanarSection
{
    /// <summary>
    /// The two LIMITS of a section through a plane flush with a planar face — the primitive a
    /// flush section has, since there is no cross-section curve there to be the answer.
    ///
    /// <para><b>Why a pair rather than one region.</b> Three consumers want three things:
    /// OpenSCAD's <c>projection(cut = true)</c> is the set-theoretic <c>solid ∩ plane</c>, a
    /// drawing's section view wants the material the plane actually CUTS, and
    /// <c>Shape.Section</c>'s own contract promises the curve bounding a cross-section. So the
    /// caller states which it means; <see cref="FlushLimits.Union"/> derives the set-theoretic
    /// answer from the pair rather than being a fourth one.</para>
    ///
    /// <para><b>Each limit is EXACT and the approximation is named.</b> Both are ordinary
    /// transversal sections of their own nudged planes, so the only thing approximated is that
    /// a plane at <c>±δ</c> is not the limit AT the flush plane: where the boundary is locally
    /// a vertical prism — which is what a flush face makes, and every case this exists for —
    /// the section is identical for every small δ and the limit is reproduced exactly; where a
    /// wall is sloped it differs by <c>δ·tan(slope)</c>. <see cref="FlushLimits.Nudge"/>
    /// reports the δ that was spent.</para>
    ///
    /// <para><b>The naive repair is NOT a limit</b> and this exists to replace it: letting each
    /// flush face contribute its own region and unioning with the transversal sections returns,
    /// for a fused step block (slab footprint A under a boss footprint B ⊂ A sectioned at the
    /// step), exactly <c>A∖B</c> — a region neither limit ever takes, since the limit from
    /// below is A and the limit from above is B.</para>
    /// </summary>
    /// <exception cref="FlushSectionException">Every nudged plane the ladder tried was itself
    /// flush with some face of the solid.</exception>
    public static FlushLimits FlushLimitsOf(
        BrepSolid solid, in Frame3d plane, double chordTolerance = DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(solid);
        double nudge = ResolveNudge(solid, plane);
        return new FlushLimits(
            OfSolid(solid, Shift(plane, -nudge), chordTolerance),
            OfSolid(solid, Shift(plane, nudge), chordTolerance),
            nudge);
    }

    /// <inheritdoc cref="FlushLimitsOf"/>
    public static CurvedFlushLimits CurvedFlushLimitsOf(
        BrepSolid solid, in Frame3d plane, double chordTolerance = DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(solid);
        double nudge = ResolveNudge(solid, plane);
        return new CurvedFlushLimits(
            CurvedOfSolid(solid, Shift(plane, -nudge), chordTolerance),
            CurvedOfSolid(solid, Shift(plane, nudge), chordTolerance),
            nudge);
    }

    /// <summary>
    /// True when <paramref name="plane"/> contains a whole planar face of the solid, or a whole
    /// edge of it — the two configurations <see cref="OfSolid"/> refuses. Asked rather than
    /// restated: it reads the SAME two predicates the refusal fires from.
    /// </summary>
    public static bool IsFlushWith(BrepSolid solid, in Frame3d plane)
    {
        ArgumentNullException.ThrowIfNull(solid);
        var origin = plane.Origin;
        var normal = plane.Z;
        double weld = Tolerance.Default.Linear;
        foreach (var face in solid.Faces)
        {
            if (!BoundsStraddle(face.Bounds(), origin, normal))
                continue;
            if (IsFlushFace(face, origin, normal))
                return true;
        }
        foreach (var edge in solid.Edges)
        {
            if (IsInPlaneEdge(edge, origin, normal, weld))
                return true;
        }
        return false;
    }

    /// <summary>The solid's own diagonal, from the faces' bounds — the scale every relative
    /// tolerance here is measured against.</summary>
    internal static double SolidDiagonal(BrepSolid solid)
    {
        Aabb? bounds = null;
        foreach (var face in solid.Faces)
            bounds = bounds is null ? face.Bounds() : bounds.Value.Union(face.Bounds());
        return bounds is null ? 0 : bounds.Value.Size.Length;
    }

    private static Frame3d Shift(in Frame3d plane, double distance) =>
        Frame3d.FromOrthonormal(plane.Origin + plane.Z * distance, plane.X, plane.Y);

    /// <summary>
    /// The nudge, halved until BOTH sides land on a plane that is not itself flush. A ladder
    /// rather than one value because a model can legitimately carry a second face exactly one
    /// nudge away; exhausting it refuses by name rather than sectioning a plane that is still
    /// flush, which would throw from three stages down with a message about the wrong plane.
    /// </summary>
    private static double ResolveNudge(BrepSolid solid, in Frame3d plane)
    {
        double diagonal = SolidDiagonal(solid);
        if (!(diagonal > 0))
            throw new FlushSectionException("The solid has no extent, so a flush section has no limits to read.");

        double nudge = FlushNudgeFraction * diagonal;
        for (int i = 0; i < 8; i++, nudge *= 0.5)
        {
            if (!IsFlushWith(solid, Shift(plane, -nudge)) && !IsFlushWith(solid, Shift(plane, nudge)))
                return nudge;
        }
        throw new FlushSectionException(
            $"Every nudged plane between {FlushNudgeFraction * diagonal:R} and {nudge * 2:R} of the flush plane is "
            + "itself flush with a face of this solid, so neither limit can be read. Section a plane that is not "
            + "flush, or move the faces apart.");
    }

    /// <summary>Applies a caller's <see cref="FlushSection"/> choice, or hands back null when
    /// the plane is transversal and the ordinary path should run unchanged.</summary>
    private static IReadOnlyList<Region2d>? TryFlush(
        BrepSolid solid, in Frame3d plane, double chordTolerance, FlushSection flush)
    {
        if (flush == FlushSection.Refuse || !IsFlushWith(solid, plane))
            return null;
        var limits = FlushLimitsOf(solid, plane, chordTolerance);
        return flush switch
        {
            FlushSection.Below => limits.Below,
            FlushSection.Above => limits.Above,
            FlushSection.Union => limits.Union(),
            _ => null,
        };
    }

    private static IReadOnlyList<CurvedRegion2d>? TryCurvedFlush(
        BrepSolid solid, in Frame3d plane, double chordTolerance, FlushSection flush)
    {
        if (flush == FlushSection.Refuse || !IsFlushWith(solid, plane))
            return null;
        var limits = CurvedFlushLimitsOf(solid, plane, chordTolerance);
        return flush switch
        {
            FlushSection.Below => limits.Below,
            FlushSection.Above => limits.Above,
            FlushSection.Union => limits.Union(),
            _ => null,
        };
    }
}
