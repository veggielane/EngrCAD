using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// Field display in the browser frame, asserted as values. Nothing about what a field
/// LOOKS like is decided in EngrCAD.Web — the colours come from
/// <c>FieldRendering</c>/<c>ColorMaps</c> and the legend layout from
/// <c>FieldLegend</c>, all in EngrCAD.Viewer.Core — so what these tests pin is the
/// plumbing: which draws carry <c>uFieldColor</c>, where the ghost pass sits, and that
/// the legend's draws take their colours and ranges FROM the shared geometry rather
/// than from a copy.
/// </summary>
public class FieldFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    private static ViewportInstance Instance(
        string key, bool fieldColored = false, string? ghost = null,
        DisplayMode mode = DisplayMode.Shaded, bool wireFieldColored = false) =>
        new(key, Matrix4d.Identity, Palette.Brass, Vector3d.Zero, mode,
            EdgeKey: key + ".edges", EdgeVertexCount: 12,
            WireKey: key + ".wire", WireVertexCount: 24,
            Visible: true, ClippedBySection: true,
            FieldColored: fieldColored, GhostKey: ghost,
            WireFieldColored: wireFieldColored);

    private static ResolvedFieldDisplay Display(MeshField? deform = null, double scale = 1) =>
        new(MeshField.Scalar("von Mises", "MPa", [0, 10, 40]),
            new FieldRange(0, 40), FieldColorMap.Viridis, deform, scale, true);

    private static ViewportLegend Legend(double width = 900, double height = 700) =>
        new(ViewportFrame.LegendBandsKey, ViewportFrame.LegendLinesKey,
            FieldLegend.Build(Display(), width, height));

    private static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances, ViewportLegend? legend = null,
        ViewStyle style = ViewStyle.ShadedWithEdges) =>
        ViewportFrame.Build(instances, Camera, Bounds, aspect: 1.6, furniture: null, style,
            legend: legend);

    [Fact]
    public void AWireframePartWithWireColours_DrawsItsResult()
    {
        // The wire upload carries per-endpoint colours, so the wireframe draw turns the
        // strength up; a wireframe part WITHOUT them (no field, or a cell field) says
        // nothing and keeps the line program's neutral 0.
        var coloured = Build([Instance("a", fieldColored: true,
            mode: DisplayMode.Wireframe, wireFieldColored: true)]);
        var wire = Assert.Single(coloured.Draws, d => d.Geometry == "a.wire");
        Assert.Equal(FieldRendering.Strength, wire.Uniforms!["uFieldColor"]);

        var plain = Build([Instance("b", mode: DisplayMode.Wireframe)]);
        var plainWire = Assert.Single(plain.Draws, d => d.Geometry == "b.wire");
        Assert.False(plainWire.Uniforms!.ContainsKey("uFieldColor"));
    }

    [Fact]
    public void APointsPartWithAField_DrawsItsResult()
    {
        // Points is a GLOBAL view style (default-mode parts follow it); the sprites
        // draw the mesh upload, which already carries the colour buffer — one uniform
        // and they read the result.
        var frame = Build([Instance("a", fieldColored: true)], style: ViewStyle.Points);
        var points = Assert.Single(frame.Draws, d => d.Mode == "points");
        Assert.Equal(FieldRendering.Strength, points.Uniforms!["uFieldColor"]);

        var plain = Build([Instance("b")], style: ViewStyle.Points);
        var plainPoints = Assert.Single(plain.Draws, d => d.Mode == "points");
        Assert.False(plainPoints.Uniforms!.ContainsKey("uFieldColor"));
    }

    [Fact]
    public void SelectionKeepsTheHighlightOnAFieldColouredWireframe()
    {
        // With no fill, the line colour is selection's only channel — so a selected
        // wireframe part draws the highlight, not the field.
        var frame = ViewportFrame.Build(
            [Instance("a", fieldColored: true, mode: DisplayMode.Wireframe, wireFieldColored: true)],
            Camera, Bounds, aspect: 1.6, furniture: null, ViewStyle.ShadedWithEdges,
            selected: 0);
        var wire = Assert.Single(frame.Draws, d => d.Geometry == "a.wire");
        Assert.False(wire.Uniforms!.ContainsKey("uFieldColor"));
    }

    [Fact]
    public void TheSharedFieldUniformIsNeutral()
    {
        // The rule that makes a fieldless part render byte-identically: the frame carries
        // uFieldColor 0, so mix(uColor, vFieldColor, 0.0) is uColor.
        Assert.Equal(0f, Build([Instance("a")]).Shared!["uFieldColor"]);
    }

    [Fact]
    public void APartWithoutAFieldSaysNothingAboutIt()
    {
        var frame = Build([Instance("a")]);
        var fill = Assert.Single(
            frame.Draws, d => d.Program == ViewportFrame.MeshProgram && d.Geometry == "a");
        Assert.False(fill.Uniforms!.ContainsKey("uFieldColor"));
    }

    [Fact]
    public void AFieldColoredPartOverridesTheUniformOnItsFill()
    {
        var frame = Build([Instance("a", fieldColored: true)]);
        var fill = Assert.Single(
            frame.Draws, d => d.Program == ViewportFrame.MeshProgram && d.Geometry == "a");
        Assert.Equal(FieldRendering.Strength, fill.Uniforms!["uFieldColor"]);
    }

    [Fact]
    public void ATranslucentFieldColoredPartCarriesItTooAndKeepsItsAlpha()
    {
        var frame = Build([Instance("a", fieldColored: true, mode: DisplayMode.Translucent)]);
        var fill = Assert.Single(
            frame.Draws, d => d.Program == ViewportFrame.MeshProgram && d.Geometry == "a");
        Assert.Equal(FieldRendering.Strength, fill.Uniforms!["uFieldColor"]);
        Assert.Equal(ViewportFrame.TranslucentAlpha, fill.Uniforms["uAlpha"]);
        Assert.True(fill.Blend);
    }

    [Fact]
    public void TheGhostDrawsBlendedAfterEveryFillAndCarriesNoField()
    {
        var frame = Build([Instance("a", fieldColored: true, ghost: "a.ghost")]);
        var draws = frame.Draws;
        int fill = draws.ToList().FindIndex(d => d.Geometry == "a");
        int ghost = draws.ToList().FindIndex(d => d.Geometry == "a.ghost");

        Assert.True(ghost > fill, "the ghost must draw after the shape it sits behind");
        var call = draws[ghost];
        Assert.Equal(ViewportFrame.MeshProgram, call.Program);
        Assert.True(call.Blend);
        Assert.False(call.DepthWrite);      // never hides the result in front of it
        Assert.False(call.Cull);
        Assert.Equal(FieldRendering.GhostAlpha, call.Uniforms!["uAlpha"]);
        // The ghost is the part's own colour, never colour-mapped: it carries no colour
        // buffer, so leaving uFieldColor at the frame's 0 is what draws it correctly.
        Assert.False(call.Uniforms.ContainsKey("uFieldColor"));
    }

    [Fact]
    public void AHiddenInstanceContributesNoGhost()
    {
        var hidden = Instance("a", fieldColored: true, ghost: "a.ghost") with { Visible = false };
        Assert.DoesNotContain(Build([hidden]).Draws, d => d.Geometry == "a.ghost");
    }

    [Fact]
    public void TheLegendDrawsOneCallPerBandInTheSharedColors()
    {
        var legend = Legend();
        var frame = Build([Instance("a", fieldColored: true)], legend);
        var bands = frame.Draws.Where(d => d.Geometry == ViewportFrame.LegendBandsKey).ToList();

        Assert.Equal(FieldLegend.Bands, bands.Count);
        for (int b = 0; b < bands.Count; b++)
        {
            // Colours are READ FROM the shared geometry, never recomputed here — a copied
            // colour rule would agree with a broken implementation as happily as a
            // correct one.
            var (r, g, blue) = legend.Geometry.BandColors[b];
            Assert.Equal([r, g, blue], (float[])bands[b].Uniforms!["uColor"]);
            Assert.Equal("triangles", bands[b].Mode);
            Assert.Equal(b * FieldLegend.VerticesPerBand, bands[b].First);
            Assert.Equal(FieldLegend.VerticesPerBand, bands[b].Count);
            Assert.False(bands[b].DepthTest);   // chrome, always on top
            Assert.False(bands[b].Cull);
        }
    }

    [Fact]
    public void TheLegendOutlineAndLabelsAreTwoRangesOfOneBuffer()
    {
        var legend = Legend();
        var frame = Build([Instance("a", fieldColored: true)], legend);
        var lines = frame.Draws.Where(d => d.Geometry == ViewportFrame.LegendLinesKey).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].First);
        Assert.Equal(legend.Geometry.FrameVertexCount, lines[0].Count);
        Assert.Equal(legend.Geometry.FrameVertexCount, lines[1].First);
        Assert.Equal(legend.Geometry.LabelVertexCount, lines[1].Count);
        Assert.Equal(
            [FieldLegend.FrameColor.R, FieldLegend.FrameColor.G, FieldLegend.FrameColor.B],
            (float[])lines[0].Uniforms!["uColor"]);
        Assert.Equal(
            [FieldLegend.LabelColor.R, FieldLegend.LabelColor.G, FieldLegend.LabelColor.B],
            (float[])lines[1].Uniforms!["uColor"]);
    }

    [Fact]
    public void TheLegendUsesItsOwnPixelProjectionOverAnIdentityView()
    {
        var legend = Legend();
        var frame = Build([Instance("a", fieldColored: true)], legend);
        var band = frame.Draws.First(d => d.Geometry == ViewportFrame.LegendBandsKey);

        var identity = new float[16];
        CameraMath.WriteColumnMajor(Matrix4d.Identity, identity);
        var projection = new float[16];
        CameraMath.WriteColumnMajor(legend.Geometry.Projection, projection);
        Assert.Equal(identity, (float[])band.Uniforms!["uModel"]);
        Assert.Equal(identity, (float[])band.Uniforms["uView"]);
        Assert.Equal(projection, (float[])band.Uniforms["uProj"]);
    }

    [Fact]
    public void TheLegendDrawsBeforeTheViewCube()
    {
        // Pass order, the desktop's: the legend is documentation about the model, the
        // cube is window chrome sitting over everything.
        var frame = ViewportFrame.Build(
            [Instance("a", fieldColored: true)], Camera, Bounds, aspect: 1.6,
            cube: new ViewportCube(900, 700), legend: Legend());
        var draws = frame.Draws.ToList();
        int legend = draws.FindLastIndex(d => d.Geometry == ViewportFrame.LegendBandsKey);
        int cube = draws.FindIndex(d => d.Geometry == ViewportFrame.CubeFillsKey);
        Assert.True(legend >= 0 && cube > legend, "the cube must draw over the legend");
    }

    [Fact]
    public void NoLegendWhenNoneIsSupplied()
    {
        Assert.DoesNotContain(
            Build([Instance("a", fieldColored: true)]).Draws,
            d => d.Geometry == ViewportFrame.LegendBandsKey);
    }

    [Fact]
    public void ALegendTooSmallToLayOutContributesNoDraws()
    {
        // FieldLegend refuses a viewport it cannot draw a readable widget in; the frame
        // must then contain nothing rather than a zero-band bar.
        var tiny = new ViewportLegend(
            ViewportFrame.LegendBandsKey, ViewportFrame.LegendLinesKey,
            FieldLegend.Build(Display(), 60, 60));
        Assert.False(tiny.Geometry.HasContent);
        Assert.DoesNotContain(
            Build([Instance("a", fieldColored: true)], tiny).Draws,
            d => d.Geometry == ViewportFrame.LegendBandsKey);
    }
}
