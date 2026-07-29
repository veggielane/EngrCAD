using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// The deformation uniform in the browser frame, asserted as values.
/// <para>The claim under test is the whole point of the design: animating a structural
/// result changes ONE float uniform per frame and nothing else — same geometry keys, same
/// draw count, same order. A pixel test could not distinguish that from a re-upload;
/// a frame description can.</para>
/// </summary>
public class DeformFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    private static ViewportInstance Instance(string key, double deformScale = 0) =>
        new(key, Matrix4d.Identity, Palette.Brass, Vector3d.Zero, DisplayMode.Shaded,
            Visible: true, ClippedBySection: true,
            FieldColored: deformScale != 0, DeformScale: deformScale);

    private static FrameDescription Build(IReadOnlyList<ViewportInstance> instances, double factor = 1) =>
        ViewportFrame.Build(instances, Camera, Bounds, aspect: 1.6, deformFactor: factor);

    private static object? Uniform(DrawCall draw, string name) =>
        draw.Uniforms is { } u && u.TryGetValue(name, out var value) ? value : null;

    [Fact]
    public void TheFrameDefaultsTheDeformUniformToZero()
    {
        // The neutral value, exactly as uFieldColor and uHighlight are neutral by
        // default: a draw that says nothing must not inherit the previous draw's scale.
        var shared = Assert.IsType<Dictionary<string, object>>(Build([Instance("a")]).Shared);
        Assert.Equal(0f, shared["uDeformScale"]);
    }

    [Fact]
    public void AnUndisplacedInstanceSaysNothingAboutTheScale()
    {
        var draw = Assert.Single(Build([Instance("a")]).Draws, d => d.Geometry == "a");
        Assert.Null(Uniform(draw, "uDeformScale"));
    }

    [Fact]
    public void ADisplacedInstanceSendsItsOwnScaleTimesTheFactor()
    {
        var frame = Build([Instance("a", deformScale: 40)], factor: 0.25);
        var draw = Assert.Single(frame.Draws, d => d.Geometry == "a");
        Assert.Equal(10f, Uniform(draw, "uDeformScale"));
    }

    [Fact]
    public void TheProductIsFormedInDoubleAndNarrowedOnce()
    {
        // The byte-equality argument in miniature: a part at s animated to factor f must
        // send the identical float a part displayed at s*f sends, so the multiply cannot
        // happen after a narrowing.
        const double stated = 0.1, factor = 0.3;
        var animated = Assert.Single(
            Build([Instance("a", stated)], factor).Draws, d => d.Geometry == "a");
        var still = Assert.Single(
            Build([Instance("a", stated * factor)]).Draws, d => d.Geometry == "a");
        Assert.Equal(Uniform(still, "uDeformScale"), Uniform(animated, "uDeformScale"));
    }

    [Fact]
    public void ChangingTheFactorChangesOnlyThatUniform()
    {
        // THE claim: an animated result is one uniform per frame. Same geometry keys, the
        // same number of draws in the same order, every other uniform identical.
        var instances = new[] { Instance("a", 40), Instance("b") };
        var at1 = Build(instances).Draws;
        var atHalf = Build(instances, 0.5).Draws;

        Assert.Equal(at1.Count, atHalf.Count);
        for (int i = 0; i < at1.Count; i++)
        {
            Assert.Equal(at1[i].Geometry, atHalf[i].Geometry);
            Assert.Equal(at1[i].Program, atHalf[i].Program);
            var before = at1[i].Uniforms ?? [];
            var after = atHalf[i].Uniforms ?? [];
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (name, value) in before)
            {
                if (name == "uDeformScale")
                    continue;
                Assert.Equal(value, after[name]);
            }
        }
        // ... and exactly one draw's uDeformScale moved (the displaced part's fill).
        int moved = 0;
        for (int i = 0; i < at1.Count; i++)
        {
            if (!Equals(Uniform(at1[i], "uDeformScale"), Uniform(atHalf[i], "uDeformScale")))
                moved++;
        }
        Assert.Equal(1, moved);
    }

    [Fact]
    public void ThePointsViewFollowsTheDisplacement()
    {
        // The points view draws the mesh buffer, so it must displace with it — the CPU
        // path gave it that for free by uploading displaced positions.
        var frame = ViewportFrame.Build(
            [Instance("a", deformScale: 40)], Camera, Bounds, aspect: 1.6,
            style: ViewStyle.Points, deformFactor: 0.5);
        var points = Assert.Single(frame.Draws, d => d.Mode == "points");
        Assert.Equal(ViewportFrame.PointProgram, points.Program);
        Assert.Equal(20f, Uniform(points, "uDeformScale"));
    }

    [Fact]
    public void TheGhostPassNeverCarriesAScale()
    {
        // The ghost is its own UNDEFORMED upload — the reference outline keeps the part's
        // own face normals and no displacement buffer — so its draw says nothing about
        // the scale and inherits the frame's neutral 0.
        var ghosted = new ViewportInstance(
            "a", Matrix4d.Identity, Palette.Brass, Vector3d.Zero, DisplayMode.Shaded,
            Visible: true, ClippedBySection: true,
            FieldColored: true, GhostKey: "a.ghost", DeformScale: 40);
        var ghost = Assert.Single(Build([ghosted], 0.5).Draws, d => d.Geometry == "a.ghost");
        Assert.Null(Uniform(ghost, "uDeformScale"));
        Assert.Equal(FieldRendering.GhostAlpha, ghost.Uniforms!["uAlpha"]);
    }
}
