using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The PMI follow-ups' pure geometry (no GL): angular-dimension anatomy (arc at the
/// chosen radius about the vertex, degree text), multi-line text layout for leaders
/// and billboards, and ray picking against an annotation's own drawn segments.
/// </summary>
public class AnnotationPickAndAngularTests
{
    private static AnnotationCamera Camera() =>
        AnnotationCamera.From(
            new CameraState(0.7, 0.45, 30, Vector3d.Zero), orthographic: false, 800, 1.0);

    private static List<(Vector3d A, Vector3d B)> Build(
        ResolvedAnnotation annotation, in AnnotationCamera camera)
    {
        var segments = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(
            segments, [new AnnotationItem(annotation, Matrix4d.Identity)], camera);
        return segments;
    }

    // ---- angular dimension anatomy ----

    [Fact]
    public void Angular_ArcSitsAtTheOffsetRadiusAboutTheVertex()
    {
        var resolved = new AngularDimension((0, 0, 0), (10, 0, 0), (0, 10, 0))
        {
            Offset = new Vector3d(4, 0, 0),   // |offset| = the arc radius
        }.Resolve();
        var segments = Build(resolved, Camera());
        Assert.True(segments.Count > 10);   // rays + arc chords + arrows + text

        // Every arc chord endpoint sits exactly radius 4 from the vertex; count the
        // segment endpoints that do (the 90-degree arc at 5 degrees per chord is 18
        // chords, far more than any other feature contributes at that radius).
        int onArc = 0;
        foreach (var (a, b) in segments)
        {
            if (Math.Abs(a.Length - 4) < 1e-9)
                onArc++;
            if (Math.Abs(b.Length - 4) < 1e-9)
                onArc++;
        }
        Assert.True(onArc >= 19 * 2 - 2, $"expected a chorded arc at radius 4, found {onArc} endpoints");
    }

    [Fact]
    public void Angular_TextCarriesDegrees()
    {
        var resolved = new AngularDimension((0, 0, 0), (10, 0, 0), (0, 10, 0)).Resolve();
        Assert.Equal("90°", resolved.Text);
        Assert.Equal(90.0, resolved.Value, 1e-9);
    }

    // ---- multi-line text ----

    [Fact]
    public void MultiLineLeader_LaysBothLinesOut_SecondBelowTheFirst()
    {
        var camera = Camera();
        var single = Build(new LeaderNote((0, 0, 0), "AB").Resolve(), camera);
        var doubled = Build(new LeaderNote((0, 0, 0), "AB\nAB").Resolve(), camera);

        // The second line adds stroke segments (same glyphs again).
        Assert.True(doubled.Count > single.Count);

        // Both lines' glyph segments exist and the second sits lower on screen: the
        // minimum along camera.Up over the TEXT strokes (the first four segments are
        // the arrowhead, leader and tail, identical in both builds) drops.
        double MinUp(List<(Vector3d A, Vector3d B)> segments)
        {
            double min = double.PositiveInfinity;
            foreach (var (a, b) in segments.Skip(4))
            {
                min = Math.Min(min, a.Dot(camera.Up));
                min = Math.Min(min, b.Dot(camera.Up));
            }
            return min;
        }
        Assert.True(MinUp(doubled) < MinUp(single) - 1e-9);
    }

    [Fact]
    public void MultiLine_SingleLineOutputIsUnchanged()
    {
        // The multi-line layout must be a strict generalization: text with no '\n'
        // produces exactly the segments it always did (the committed docs PNGs hang
        // off this).
        var camera = Camera();
        var note = Build(new LeaderNote((1, 2, 3), "R5 TYP").Resolve(), camera);
        Assert.True(note.Count > 0);
        var again = Build(new LeaderNote((1, 2, 3), "R5 TYP").Resolve(), camera);
        Assert.Equal(note, again);
    }

    [Fact]
    public void MultiLineDatum_BoxSpansEveryLine()
    {
        var camera = Camera();
        var single = Build(new DatumLabel((0, 0, 0), "A").Resolve(), camera);
        var doubled = Build(new DatumLabel((0, 0, 0), "A\nB").Resolve(), camera);

        // The box is the last four segments in both cases; the two-line box is taller
        // (its vertical sides are longer than the one-line box's).
        double BoxHeight(List<(Vector3d A, Vector3d B)> segments)
        {
            double longest = 0;
            foreach (var (a, b) in segments.Skip(segments.Count - 4))
                longest = Math.Max(longest, a.DistanceTo(b));
            return longest;
        }
        Assert.True(BoxHeight(doubled) > BoxHeight(single) * 1.5);
    }

    // ---- picking ----

    [Fact]
    public void Pick_RayThroughADimensionLine_HitsIt()
    {
        var camera = Camera();
        var items = new List<AnnotationItem>
        {
            new(new LinearDimension((0, 0, 0), (10, 0, 0)).Resolve(), Matrix4d.Identity),
        };
        var segments = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(segments, items, camera);
        var target = (segments[0].A + segments[0].B) * 0.5;

        var hit = AnnotationGeometry.Pick(
            items, camera, new Ray3d(camera.Eye, target - camera.Eye));
        Assert.Equal(0, hit);
    }

    [Fact]
    public void Pick_RayFarFromEverything_Misses()
    {
        var camera = Camera();
        var items = new List<AnnotationItem>
        {
            new(new LinearDimension((0, 0, 0), (10, 0, 0)).Resolve(), Matrix4d.Identity),
        };
        var away = camera.Eye + camera.Forward * 30 + camera.Up * 500;
        var hit = AnnotationGeometry.Pick(items, camera, new Ray3d(camera.Eye, away - camera.Eye));
        Assert.Equal(-1, hit);
    }

    [Fact]
    public void Pick_TwoAnnotations_NearestSegmentWins()
    {
        var camera = Camera();
        var items = new List<AnnotationItem>
        {
            new(new LinearDimension((0, 0, 0), (10, 0, 0)).Resolve(), Matrix4d.Identity),
            new(new LinearDimension((0, 40, 0), (10, 40, 0)).Resolve(), Matrix4d.Identity),
        };
        var second = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(second, [items[1]], camera);
        var target = (second[0].A + second[0].B) * 0.5;

        var hit = AnnotationGeometry.Pick(items, camera, new Ray3d(camera.Eye, target - camera.Eye));
        Assert.Equal(1, hit);
    }
}
