using EngrCAD.Core;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The section planes' clip rule as picking applies it. This must stay identical to
/// the fragment shaders' discard — per plane <c>dot(world, normal) &gt; offset</c>,
/// combined by <see cref="SectionCombine"/> — because a click may not select a surface
/// the cut removed, and multi-plane cutaways would otherwise let picking and rendering
/// disagree about which corner is gone.
/// </summary>
public class SectionPickTests
{
    [Fact]
    public void NothingIsHiddenWhileSectionModeIsOff()
    {
        Assert.False(SectionClip.Hides(false, (0, 0, 100), SectionAxis.Z, 0));
        Assert.False(SectionClip.Hides(false, (100, 0, 0), SectionAxis.X, -50));
    }

    [Theory]
    [InlineData(SectionAxis.X)]
    [InlineData(SectionAxis.Y)]
    [InlineData(SectionAxis.Z)]
    public void PointsBeyondThePlaneAreHiddenOnEveryAxis(SectionAxis axis)
    {
        var beyond = axis switch
        {
            SectionAxis.X => new Vector3d(6, 0, 0),
            SectionAxis.Y => new Vector3d(0, 6, 0),
            _ => new Vector3d(0, 0, 6),
        };
        Assert.True(SectionClip.Hides(true, beyond, axis, 5));
        Assert.False(SectionClip.Hides(true, -beyond, axis, 5));
    }

    [Fact]
    public void TheCutPlaneItselfIsVisible()
    {
        // The shader discards strictly greater than the offset, so the exposed cut
        // face (which sits exactly on the plane) stays pickable.
        Assert.False(SectionClip.Hides(true, (0, 0, 5), SectionAxis.Z, 5));
        Assert.True(SectionClip.Hides(true, (0, 0, 5.0001), SectionAxis.Z, 5));
    }

    [Fact]
    public void OtherAxesDoNotAffectTheTest()
    {
        // Only the active axis' component matters — a far-out X does not hide a point
        // when the plane cuts Z.
        Assert.False(SectionClip.Hides(true, (999, -999, 1), SectionAxis.Z, 5));
    }

    // ---- several planes at once: picking must agree with the shader's combine rule ----

    private static readonly SectionPlane[] QuarterCut =
        [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0)];

    [Fact]
    public void IntersectionHidesOnlyTheCornerWhereEveryPlaneExcludes()
    {
        // The quarter cut removes the +x+y quadrant and nothing else: a point excluded
        // by one plane alone is still material, and still clickable.
        Assert.True(SectionClip.Hides(true, (5, 5, 0), QuarterCut, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (5, -5, 0), QuarterCut, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (-5, 5, 0), QuarterCut, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (-5, -5, 0), QuarterCut, SectionCombine.Intersection));
    }

    [Fact]
    public void UnionHidesWhereAnyPlaneExcludes()
    {
        // Each plane cuts independently, so only the quadrant every plane keeps survives.
        Assert.True(SectionClip.Hides(true, (5, 5, 0), QuarterCut, SectionCombine.Union));
        Assert.True(SectionClip.Hides(true, (5, -5, 0), QuarterCut, SectionCombine.Union));
        Assert.True(SectionClip.Hides(true, (-5, 5, 0), QuarterCut, SectionCombine.Union));
        Assert.False(SectionClip.Hides(true, (-5, -5, 0), QuarterCut, SectionCombine.Union));
    }

    [Fact]
    public void OnePlaneMakesBothCombineRulesCoincide()
    {
        SectionPlane[] single = [SectionPlane.On(SectionAxis.Z, 5)];
        foreach (var combine in new[] { SectionCombine.Intersection, SectionCombine.Union })
        {
            Assert.True(SectionClip.Hides(true, (0, 0, 6), single, combine));
            Assert.False(SectionClip.Hides(true, (0, 0, 4), single, combine));
        }
    }

    [Fact]
    public void AnOctantNeedsAllThreePlanesToExclude()
    {
        SectionPlane[] octant =
            [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0),
             SectionPlane.On(SectionAxis.Z, 4)];
        Assert.True(SectionClip.Hides(true, (5, 5, 9), octant, SectionCombine.Intersection));
        // Below the z plane: two of three exclude, so the material stays.
        Assert.False(SectionClip.Hides(true, (5, 5, 1), octant, SectionCombine.Intersection));
    }

    [Fact]
    public void NoPlanesHidesNothingEvenWhenEnabled()
    {
        Assert.False(SectionClip.Hides(true, (99, 99, 99), [], SectionCombine.Intersection));
    }

    [Fact]
    public void GeneralNonAxisAlignedPlanesWork()
    {
        // The shader has always taken a general normal; the CPU rule must too.
        SectionPlane[] diagonal = [new(new Vector3d(1, 1, 0).Normalized(), 0)];
        Assert.True(SectionClip.Hides(true, (1, 1, 0), diagonal, SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, (-1, -1, 0), diagonal, SectionCombine.Intersection));
    }

    // ---- arbitrary orientation: a plane placed by a frame ----

    [Fact]
    public void OnAFrame_PutsTheOriginOnThePlaneAndClipsAlongItsZ()
    {
        // The whole point of the Frame3d factory: a cut can face anywhere, because the
        // shaders and this rule have always taken a general normal — only the toolbar's
        // axis cycler is restricted to X/Y/Z.
        var frame = Frame3d.FromNormal((1, 2, 3), new Vector3d(1, 1, 1).Normalized());
        var plane = SectionPlane.On(frame);

        // The frame origin lies exactly ON the plane, so it survives the strict test.
        Assert.False(SectionClip.Hides(true, frame.Origin, [plane], SectionCombine.Intersection));
        // Along +Z is the clipped side, along -Z the kept one.
        Assert.True(SectionClip.Hides(true, frame.Origin + frame.Z, [plane], SectionCombine.Intersection));
        Assert.False(SectionClip.Hides(true, frame.Origin - frame.Z, [plane], SectionCombine.Intersection));
        // Sliding within the plane changes nothing.
        Assert.False(SectionClip.Hides(true, frame.Origin + frame.X * 9 - frame.Y * 4,
            [plane], SectionCombine.Intersection));
    }

    [Fact]
    public void ThroughAPoint_IsTheSamePlaneAsTheFrameForm()
    {
        var normal = new Vector3d(0, 3, 4).Normalized();   // 3-4-5, exactly representable
        var point = new Vector3d(-2, 5, 1);
        var frame = Frame3d.FromNormal(point, normal);
        var a = SectionPlane.On(frame);
        var b = SectionPlane.Through(point, normal);

        foreach (var probe in new Vector3d[] { (0, 0, 0), (10, -3, 7), point, point + normal })
        {
            Assert.Equal(
                SectionClip.Hides(true, probe, [a], SectionCombine.Intersection),
                SectionClip.Hides(true, probe, [b], SectionCombine.Intersection));
        }
    }

    [Fact]
    public void AnObliquePlaneQuarterCutsLikeAnAxisAlignedOne()
    {
        // Two 45-degree planes: the combine rules must not care about orientation.
        SectionPlane[] cut =
        [
            SectionPlane.Through((0, 0, 0), new Vector3d(1, 1, 0).Normalized()),
            SectionPlane.Through((0, 0, 0), new Vector3d(1, -1, 0).Normalized()),
        ];
        // +X is excluded by BOTH (dot > 0 either way), so Intersection removes it.
        Assert.True(SectionClip.Hides(true, (5, 0, 0), cut, SectionCombine.Intersection));
        // +Y is excluded by the first only.
        Assert.False(SectionClip.Hides(true, (0, 5, 0), cut, SectionCombine.Intersection));
        Assert.True(SectionClip.Hides(true, (0, 5, 0), cut, SectionCombine.Union));
        Assert.False(SectionClip.Hides(true, (-5, 0, 0), cut, SectionCombine.Union));
    }

    // ---- the cut-face (sibling) rule that clips anything drawn ON a section plane ----

    /// <summary>How far <c>SectionContours</c> lifts its lines to the kept side of the
    /// plane they belong to (a fraction of the level spacing) — modelled here so the
    /// samples sit where the renderer actually draws them.</summary>
    private const double Lift = 1e-3;

    /// <summary>
    /// The ground truth <see cref="SectionClip.Siblings"/> must reproduce, stated
    /// independently of it: plane <paramref name="index"/>'s cut face is visible at
    /// <paramref name="onPlane"/> iff the drawn (lifted) line survives the full clip
    /// rule AND the material just past the plane does not. Derived only from
    /// <c>Hides</c>, so a change to either rule breaks the tests below rather than
    /// silently agreeing with them.
    /// </summary>
    private static bool CutFaceIsExposed(
        in Vector3d onPlane, IReadOnlyList<SectionPlane> planes, int index, SectionCombine combine)
    {
        var normal = planes[index].Normal.Normalized();
        // Step far enough past the plane that no float rounding can leave the sample on
        // the wrong side; the half-spaces are unbounded, so any positive step decides it.
        return !SectionClip.Hides(true, onPlane - normal * Lift, planes, combine)
            && SectionClip.Hides(true, onPlane + normal, planes, combine);
    }

    private static bool SiblingsHide(
        in Vector3d onPlane, IReadOnlyList<SectionPlane> planes, int index, SectionCombine combine)
    {
        var siblings = new List<SectionPlane>();
        SectionClip.Siblings(planes, index, combine, siblings);
        return SectionClip.Hides(
            true, onPlane - planes[index].Normal.Normalized() * Lift, siblings, SectionCombine.Union);
    }

    /// <summary>Sample points on plane <paramref name="index"/> covering every
    /// combination of sides of the other planes (the quadrant/octant corners).</summary>
    private static IEnumerable<Vector3d> PointsOnPlane(IReadOnlyList<SectionPlane> planes, int index)
    {
        var plane = planes[index];
        var normal = plane.Normal.Normalized();
        foreach (double a in new[] { -7.0, 7.0 })
        {
            foreach (double b in new[] { -7.0, 7.0 })
            {
                foreach (double c in new[] { -7.0, 7.0 })
                {
                    // Project the sample onto plane `index` (to within rounding — which
                    // is exactly why the renderer lifts its lines off the plane).
                    var raw = new Vector3d(a, b, c);
                    yield return raw + normal * (plane.Offset / plane.Normal.Length - raw.Dot(normal));
                }
            }
        }
    }

    [Theory]
    [InlineData(SectionCombine.Intersection)]
    [InlineData(SectionCombine.Union)]
    public void SiblingClipKeepsExactlyTheExposedCutFace(SectionCombine combine)
    {
        // Every plane set the UI can produce, checked against the independent
        // exposed-cut-face definition above. This is the isoline overlay's rule: without
        // it a quarter cut draws each plane's contours across its full extent, half of
        // them buried inside the remaining material.
        SectionPlane[][] sets =
        [
            [SectionPlane.On(SectionAxis.Z, 0)],
            QuarterCut,
            [SectionPlane.On(SectionAxis.X, 2), SectionPlane.On(SectionAxis.Y, -3)],
            [SectionPlane.On(SectionAxis.X, 0), SectionPlane.On(SectionAxis.Y, 0),
             SectionPlane.On(SectionAxis.Z, 0)],
            [new(new Vector3d(1, 1, 0).Normalized(), 1), SectionPlane.On(SectionAxis.Z, -2)],
        ];

        foreach (var planes in sets)
        {
            for (int i = 0; i < planes.Length; i++)
            {
                foreach (var point in PointsOnPlane(planes, i))
                {
                    Assert.Equal(
                        !CutFaceIsExposed(point, planes, i, combine),
                        SiblingsHide(point, planes, i, combine));
                }
            }
        }
    }

    [Fact]
    public void SiblingClipOfASinglePlaneIsEmpty()
    {
        // One plane: its whole cut face is exposed, so the overlay must not be clipped
        // at all (an empty plane list is how SectionUniforms.Write disables clipping).
        var siblings = new List<SectionPlane> { SectionPlane.On(SectionAxis.X, 0) };
        SectionClip.Siblings([SectionPlane.On(SectionAxis.Z, 3)], 0, SectionCombine.Intersection, siblings);
        Assert.Empty(siblings);
    }

    [Fact]
    public void SiblingClipOfAQuarterCutIsTheOtherPlaneFlipped()
    {
        // The concrete quarter-cut case, spelled out: plane X's contours survive only
        // where plane Y excludes (y > 0), which is plane Y turned around.
        var siblings = new List<SectionPlane>();
        SectionClip.Siblings(QuarterCut, 0, SectionCombine.Intersection, siblings);
        Assert.Equal([SectionPlane.On(SectionAxis.Y, 0).Flipped()], siblings);

        // Under Union the model keeps only what every plane keeps, so plane X's face is
        // exposed where plane Y does NOT exclude — the sibling unflipped.
        SectionClip.Siblings(QuarterCut, 0, SectionCombine.Union, siblings);
        Assert.Equal([SectionPlane.On(SectionAxis.Y, 0)], siblings);
    }
}
