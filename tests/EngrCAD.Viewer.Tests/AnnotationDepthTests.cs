using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The pure half of occlusion-aware annotation rendering (<see cref="AnnotationDepth"/>):
/// the value/pointer split <c>AnnotationGeometry.Build</c> makes and the depth bias it
/// applies. No GL — the pixels are <see cref="AnnotationRenderTests"/>' business.
/// <para>These are the assertions a screenshot cannot make. Whether the text really ends
/// up in its own list, and whether the bias really moves points along the VIEW direction
/// by exactly the requested amount, are properties of values; a render can only show that
/// something looks about right.</para>
/// </summary>
public class AnnotationDepthTests
{
    private static AnnotationCamera Camera(double distance = 30, double heightPx = 800) =>
        AnnotationCamera.From(
            new CameraState(0.7, 0.45, distance, Vector3d.Zero), orthographic: false, heightPx, 1.0);

    private static AnnotationItem Item(Annotation annotation) =>
        new(annotation.Resolve(), Matrix4d.Identity);

    // ---- the value/pointer split ----

    /// <summary>
    /// The exact statement of the partition: lengthening the TEXT of an annotation must
    /// change the text list and leave the line-work list untouched, segment for segment.
    /// <para>Asserting "the text list is non-empty" would pass an implementation that put
    /// half a leader in it; comparing against the same annotation with a shorter label
    /// isolates exactly the glyph strokes, because everything else about the two builds
    /// is identical.</para>
    /// </summary>
    [Fact]
    public void LengtheningTheTextMovesOnlyTheTextList()
    {
        var camera = Camera();
        var (shortLines, shortText) = Split(Item(new LeaderNote((0, 0, 0), "1")), camera);
        var (longLines, longText) = Split(Item(new LeaderNote((0, 0, 0), "188")), camera);

        Assert.Equal(shortLines.Count, longLines.Count);
        for (int i = 0; i < shortLines.Count; i++)
            Assert.Equal(shortLines[i], longLines[i]);
        Assert.True(longText.Count > shortText.Count,
            $"more characters must add glyph strokes: {shortText.Count} then {longText.Count}");
    }

    /// <summary>A datum label's BOX follows its letter into the text list: the box frames
    /// the value and reads as part of it, so occluding one without the other would draw a
    /// floating rectangle round nothing.</summary>
    [Fact]
    public void TheDatumBoxTravelsWithItsLetter()
    {
        var camera = Camera();
        var (noteLines, noteText) = Split(Item(new LeaderNote((0, 0, 0), "A")), camera);
        var (datumLines, datumText) = Split(Item(new DatumLabel((0, 0, 0), "A")), camera);

        // The leader anatomy is the same for both; only the box differs, and it is text.
        Assert.Equal(noteLines.Count, datumLines.Count);
        Assert.Equal(noteText.Count + 4, datumText.Count);
    }

    /// <summary>
    /// Passing no text list is the incumbent single-list build: the same segments in the
    /// same ORDER, which is what keeps every always-on-top draw and
    /// <c>AnnotationGeometry.Pick</c> bit-identical to before the mode existed.
    /// </summary>
    [Fact]
    public void OneListIsTheConcatenationOfTheTwo()
    {
        var camera = Camera();
        var item = Item(new LinearDimension((0, 0, 0), (10, 0, 0)));

        var single = new List<(Vector3d A, Vector3d B)>();
        int lineWork = AnnotationGeometry.Build(single, [item], camera);
        Assert.Equal(single.Count, lineWork);   // everything counts as line work

        var (lines, text) = Split(item, camera);
        Assert.Equal(single.Count, lines.Count + text.Count);
        // A SET comparison, not a sequence one: the split reorders (all the line work,
        // then all the text) and that is the point of it. Ordered by a printed key so
        // the comparison is exact and the tuples need no IComparable.
        static string[] Sorted(IEnumerable<(Vector3d A, Vector3d B)> s) =>
            [.. s.Select(x => $"{x.A.X:R},{x.A.Y:R},{x.A.Z:R}|{x.B.X:R},{x.B.Y:R},{x.B.Z:R}")
                 .OrderBy(k => k, StringComparer.Ordinal)];
        Assert.Equal(Sorted(single), Sorted(lines.Concat(text)));
    }

    // ---- the depth bias ----

    /// <summary>
    /// The bias is a DEPTH statement: every point's view depth falls by exactly the
    /// requested number of style pixels of world size at that point, which is what makes
    /// a coplanar leader read as being on its face rather than fighting it. Verified
    /// against <c>AnnotationCamera.PxToWorld</c> at the point's own depth — the same
    /// function the overlay sizes everything else with.
    /// </summary>
    [Fact]
    public void TheBiasMovesEveryPointTowardTheEyeByExactlyOnePixel()
    {
        var camera = Camera();
        var item = Item(new LinearDimension((0, 0, 0), (10, 0, 0)));
        var plain = new List<(Vector3d A, Vector3d B)>();
        var biased = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(plain, [item], camera);
        AnnotationGeometry.Build(biased, [item], camera, AnnotationGeometry.OccludedDepthBiasPx);

        Assert.Equal(plain.Count, biased.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            double before = (plain[i].A - camera.Eye).Dot(camera.Forward);
            double after = (biased[i].A - camera.Eye).Dot(camera.Forward);
            double expected = camera.PxToWorld(AnnotationGeometry.OccludedDepthBiasPx, plain[i].A);
            Assert.Equal(expected, before - after, 12);
            Assert.True(expected > 0);
        }
    }

    /// <summary>
    /// And it changes NOTHING ELSE: the biased point stays on its own eye ray, so the
    /// direction from the eye is unchanged and the overlay's screen position is exact.
    /// <para>The first implementation translated along the view direction, which is the
    /// obvious move and slides a perspective point off its ray — it cost 134 changed
    /// pixels in a render whose overlay had nothing in front of it, purely from an
    /// anti-aliased line's coverage redistributing. This assertion is what would have
    /// caught it as a value.</para>
    /// </summary>
    [Fact]
    public void TheBiasKeepsEveryPointOnItsOwnEyeRay()
    {
        var camera = Camera();
        foreach (var point in new Vector3d[] { (10, 4, -3), (-20, 0, 8), (0, 0, 0), (5, -12, 2) })
        {
            var pulled = camera.PulledTowardEye(point, AnnotationGeometry.OccludedDepthBiasPx);
            var before = (point - camera.Eye).Normalized();
            var after = (pulled - camera.Eye).Normalized();
            Assert.Equal(1.0, before.Dot(after), 14);
            Assert.True((pulled - camera.Eye).Length < (point - camera.Eye).Length);
        }
    }

    /// <summary>Under an orthographic projection the eye rays are parallel, so the plain
    /// translation along the view direction IS the ray-preserving move — the same
    /// screen-position guarantee reached by different arithmetic.</summary>
    [Fact]
    public void TheOrthographicBiasIsAPureTranslation()
    {
        var ortho = AnnotationCamera.From(
            new CameraState(0.7, 0.45, 30, Vector3d.Zero), orthographic: true, 800, 1.0);
        Vector3d a = (10, 4, -3), b = (-20, 0, 8);
        var da = ortho.PulledTowardEye(a, 1) - a;
        var db = ortho.PulledTowardEye(b, 1) - b;
        Assert.Equal(0.0, (da - db).Length, 12);                    // the same displacement
        Assert.Equal(0.0, da.Cross(ortho.Forward).Length, 12);      // along the view direction
    }

    /// <summary>
    /// A zero bias is an exact-zero SEMANTIC test, not a zero addend: the always-on-top
    /// build must be bit-identical to what it was before the mode existed, since every
    /// committed docs render hangs off it.
    /// </summary>
    [Fact]
    public void ZeroBiasIsBitIdentical()
    {
        var camera = Camera();
        var item = Item(new LinearDimension((0, 0, 0), (10, 0, 0)));
        var a = new List<(Vector3d A, Vector3d B)>();
        var b = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(a, [item], camera);
        AnnotationGeometry.Build(b, [item], camera, depthBiasPx: 0);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].A.X), BitConverter.DoubleToInt64Bits(b[i].A.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].A.Y), BitConverter.DoubleToInt64Bits(b[i].A.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].A.Z), BitConverter.DoubleToInt64Bits(b[i].A.Z));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i].B.Z), BitConverter.DoubleToInt64Bits(b[i].B.Z));
        }
    }

    /// <summary>
    /// The bias scales with view depth, so it stays one pixel of the picture at any
    /// framing: doubling the orbit distance doubles the world offset. That is the
    /// property that makes a single constant enough — depth-buffer resolution falls as
    /// z-squared while a pixel's world size grows only as z, so a pixel-sized bias can
    /// never run out of depth bits as the camera pulls back.
    /// </summary>
    [Fact]
    public void TheBiasScalesWithTheFraming()
    {
        var item = Item(new LinearDimension((0, 0, 0), (10, 0, 0)));
        double Offset(double distance)
        {
            var camera = Camera(distance);
            var plain = new List<(Vector3d A, Vector3d B)>();
            var biased = new List<(Vector3d A, Vector3d B)>();
            AnnotationGeometry.Build(plain, [item], camera);
            AnnotationGeometry.Build(biased, [item], camera, AnnotationGeometry.OccludedDepthBiasPx);
            return (plain[0].A - biased[0].A).Length;
        }

        Assert.Equal(2 * Offset(30), Offset(60), 9);
    }

    /// <summary>The hidden colour must be DARKER than the visible one in every channel —
    /// a hidden fragment is by definition drawn over the occluder, and every part colour
    /// in the palette is a lit mid-tone brighter than the background, so darkening is the
    /// one direction that gains contrast in every case the mode can produce.</summary>
    [Fact]
    public void TheHiddenColourIsDarkerInEveryChannel()
    {
        var (r, g, b) = AnnotationGeometry.Color;
        var (hr, hg, hb) = AnnotationGeometry.HiddenColor;
        Assert.True(hr < r && hg < g && hb < b, $"({hr}, {hg}, {hb}) must be under ({r}, {g}, {b})");
    }

    private static (List<(Vector3d A, Vector3d B)> Lines, List<(Vector3d A, Vector3d B)> Text) Split(
        AnnotationItem item, in AnnotationCamera camera)
    {
        var lines = new List<(Vector3d A, Vector3d B)>();
        var text = new List<(Vector3d A, Vector3d B)>();
        int lineWork = AnnotationGeometry.Build(lines, [item], camera, depthBiasPx: 0, text);
        Assert.Equal(lines.Count, lineWork);
        return (lines, text);
    }
}
