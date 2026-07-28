using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// Section planes in the browser frame, asserted as values. The clip itself is the
/// SHARED shader rule (<c>ViewerShaders.SectionClip</c>) and the shared CPU restatement
/// (<c>SectionClip.Hides</c>, which <c>ScenePick</c> applies) — neither is restated in
/// EngrCAD.Web, so what these tests pin is the plumbing: the uniforms, the typed
/// markers, the per-draw unclip overrides, and the isoline overlay's order and palette.
/// </summary>
public class SectionFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    private static ViewportInstance Instance(
        string key, bool clipped = true, DisplayMode mode = DisplayMode.Shaded) =>
        new(key, Matrix4d.Identity, Palette.Brass, Vector3d.Zero, mode,
            EdgeKey: key + ".edges", EdgeVertexCount: 12,
            WireKey: key + ".wire", WireVertexCount: 24,
            Visible: true, ClippedBySection: clipped);

    private static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances,
        IReadOnlyList<SectionPlane>? planes,
        SectionCombine combine = SectionCombine.Intersection,
        ViewportFurniture? furniture = null,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        ViewportContours? contours = null) =>
        ViewportFrame.Build(instances, Camera, Bounds, aspect: 1.6, furniture, style,
            sectionPlanes: planes, sectionCombine: combine, contours: contours);

    [Fact]
    public void ActivePlaneSetsTheSharedSectionUniforms()
    {
        var plane = SectionPlane.On(SectionAxis.X, 2.5);
        var shared = Build([Instance("a")], [plane]).Shared!;

        Assert.Equal(1f, shared["uSectionEnabled"]);
        // The int uniform travels as the typed marker (uniform1i), never a plain number.
        Assert.Equal(new IntUniform(1), shared["uSectionCount"]);
        // Packed exactly as the desktop's SectionUniforms.Write packs them:
        // xyz = normal, w = offset, count * 4 floats.
        var packed = Assert.IsType<Vec4ArrayUniform>(shared["uSectionPlanes"]);
        Assert.Equal(
            [(float)plane.Normal.X, (float)plane.Normal.Y, (float)plane.Normal.Z, (float)plane.Offset],
            packed.Values);
        Assert.Equal(0f, shared["uSectionUnion"]);   // Intersection, the default
    }

    [Fact]
    public void UnionCombineSetsTheUnionFlag()
    {
        var shared = Build([Instance("a")],
            [SectionPlane.On(SectionAxis.Z, 1)], SectionCombine.Union).Shared!;

        Assert.Equal(1f, shared["uSectionUnion"]);
    }

    [Fact]
    public void PlaneCountClampsToTheShaderArray()
    {
        // The shader's uniform array is vec4[MaxSectionPlanes]; the desktop's
        // SectionUniforms.Write clamps rather than failing, and so does the frame.
        var planes = Enumerable.Range(0, ViewerShaders.MaxSectionPlanes + 2)
            .Select(i => SectionPlane.On(SectionAxis.Z, i))
            .ToList();
        var shared = Build([Instance("a")], planes).Shared!;

        Assert.Equal(new IntUniform(ViewerShaders.MaxSectionPlanes), shared["uSectionCount"]);
        Assert.Equal(ViewerShaders.MaxSectionPlanes * 4,
            Assert.IsType<Vec4ArrayUniform>(shared["uSectionPlanes"]).Values.Length);
    }

    [Fact]
    public void FurnitureIsNeverSectionClipped()
    {
        var furniture = new ViewportFurniture("@grid", 100, "@axes");
        var active = Build([], [SectionPlane.On(SectionAxis.Z, 1)], furniture: furniture);

        // Grid + three axes: each overrides the shared uSectionEnabled per draw, the
        // browser's version of the desktop's per-draw-group SetEnabled(false).
        var lines = active.Draws.Where(d => d.Program == ViewportFrame.LineProgram).ToList();
        Assert.Equal(4, lines.Count);
        Assert.All(lines, call => Assert.Equal(0f, call.Uniforms!["uSectionEnabled"]));

        // Neutral state says nothing — the same discipline the highlight follows.
        var neutral = Build([], null, furniture: furniture);
        Assert.All(neutral.Draws.Where(d => d.Program == ViewportFrame.LineProgram),
            call => Assert.DoesNotContain("uSectionEnabled", call.Uniforms!.Keys));
    }

    [Fact]
    public void ExemptInstanceDrawsWholeInACutaway()
    {
        // Part.ClippedBySection false — the drafting convention that a fastener stands
        // whole inside a cutaway. Its fill AND its edge overlay carry the override; an
        // ordinary neighbour carries nothing.
        var frame = Build(
            [Instance("bolt", clipped: false), Instance("housing")],
            [SectionPlane.On(SectionAxis.Z, 1)]);

        var fills = frame.Draws.Where(d => d.Program == ViewportFrame.MeshProgram).ToList();
        Assert.Equal(2, fills.Count);
        Assert.Equal(0f, fills[0].Uniforms!["uSectionEnabled"]);
        Assert.DoesNotContain("uSectionEnabled", fills[1].Uniforms!.Keys);

        var edges = frame.Draws.Where(d => d.Program == ViewportFrame.LineProgram).ToList();
        Assert.Equal(2, edges.Count);
        Assert.Equal(0f, edges[0].Uniforms!["uSectionEnabled"]);
        Assert.DoesNotContain("uSectionEnabled", edges[1].Uniforms!.Keys);
    }

    [Fact]
    public void ExemptWireframeAndPointsCarryTheOverrideToo()
    {
        var wire = Build(
            [Instance("a", clipped: false, mode: DisplayMode.Wireframe)],
            [SectionPlane.On(SectionAxis.Z, 1)]);
        var wireDraw = Assert.Single(wire.Draws, d => d.Program == ViewportFrame.LineProgram);
        Assert.Equal(0f, wireDraw.Uniforms!["uSectionEnabled"]);

        var points = Build(
            [Instance("a", clipped: false)],
            [SectionPlane.On(SectionAxis.Z, 1)], style: ViewStyle.Points);
        var pointDraw = Assert.Single(points.Draws, d => d.Program == ViewportFrame.PointProgram);
        Assert.Equal(0f, pointDraw.Uniforms!["uSectionEnabled"]);
    }

    [Fact]
    public void ExemptTranslucentInstanceCarriesTheOverride()
    {
        var frame = Build(
            [Instance("a", clipped: false, mode: DisplayMode.Translucent)],
            [SectionPlane.On(SectionAxis.Z, 1)]);

        var blended = Assert.Single(frame.Draws, d => d.Blend);
        Assert.Equal(0f, blended.Uniforms!["uSectionEnabled"]);
    }

    [Fact]
    public void ContoursDrawLastInTheSharedPalette()
    {
        var contours = new ViewportContours("@c.zero", 10, "@c.pos", 20, "@c.neg", 30);
        var frame = Build([Instance("a")], [SectionPlane.On(SectionAxis.Z, 1)],
            contours: contours);

        // Signed families first, d = 0 LAST so the gold cross-section wins overdraw —
        // the desktop renderer's order. The colours are read FROM SectionContours, so a
        // palette change lands in both front ends or in neither.
        int n = frame.Draws.Count;
        var positive = frame.Draws[n - 3];
        var negative = frame.Draws[n - 2];
        var zero = frame.Draws[n - 1];

        Assert.Equal("@c.pos", positive.Geometry);
        Assert.Equal(ToRgb(SectionContours.PositiveColor), (float[])positive.Uniforms!["uColor"]);
        Assert.Equal("@c.neg", negative.Geometry);
        Assert.Equal(ToRgb(SectionContours.NegativeColor), (float[])negative.Uniforms!["uColor"]);
        Assert.Equal("@c.zero", zero.Geometry);
        Assert.Equal(ToRgb(SectionContours.ZeroColor), (float[])zero.Uniforms!["uColor"]);

        // The geometry already sits below the plane, but a single plane has no sibling
        // clip set (SectionClip.Siblings is empty), so the draws opt out entirely.
        Assert.All(new[] { positive, negative, zero },
            call => Assert.Equal(0f, call.Uniforms!["uSectionEnabled"]));
    }

    [Fact]
    public void ContoursAreAbsentWhileSectionsAreOff()
    {
        var contours = new ViewportContours("@c.zero", 10, "@c.pos", 20, "@c.neg", 30);
        var frame = Build([Instance("a")], planes: null, contours: contours);

        Assert.DoesNotContain(frame.Draws, d => d.Geometry?.StartsWith("@c.") == true);
    }

    private static float[] ToRgb((float R, float G, float B) color) =>
        [color.R, color.G, color.B];
}
