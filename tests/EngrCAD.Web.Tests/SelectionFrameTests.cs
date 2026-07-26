using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// Visibility, selection and hover asserted as frame values.
///
/// <para>These are exactly the states a screenshot answers badly: "the selection changed
/// the picture" is true of a highlight, a re-render and a camera nudge alike. Here the
/// relationship is stated directly — hiding an instance removes its draws and moves no
/// other instance's; selecting one changes only its uniforms.</para>
///
/// <para>The highlight values are read FROM <see cref="Highlight"/> rather than repeated,
/// for the reason the display-mode tests read from <c>RenderModes.Resolve</c>: a second
/// copy of a rule agrees with a broken implementation just as happily as with a correct
/// one. What is pinned here is that the browser applies the shared rule to the right
/// draws, not what the rule says.</para>
/// </summary>
public class SelectionFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    private static ViewportInstance Instance(
        string key, DisplayMode mode = DisplayMode.Shaded, bool visible = true) =>
        new(key, Matrix4d.Identity, Palette.Brass, Vector3d.Zero, mode,
            $"{key}.edges", 24, $"{key}.wire", 60, visible);

    private static FrameDescription Build(
        IReadOnlyList<ViewportInstance> instances,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        int selected = -1, int hovered = -1) =>
        ViewportFrame.Build(instances, Camera, Bounds, aspect: 1.6, furniture: null,
            style: style, pixelScale: 1.0, selected: selected, hovered: hovered);

    private static IEnumerable<DrawCall> For(FrameDescription frame, string key) =>
        frame.Draws.Where(d => d.Geometry is { } g && g.StartsWith(key, StringComparison.Ordinal));

    // ---- visibility ----

    [Fact]
    public void AHiddenInstanceContributesNoDraws()
    {
        var shown = Build([Instance("a"), Instance("b")]);
        var hidden = Build([Instance("a"), Instance("b", visible: false)]);

        // Everything the visible instance drew is untouched; everything the hidden one
        // drew is gone, fill AND edge overlay. A viewport that hid only the fill would
        // leave a wireframe ghost, which is the bug this states away.
        Assert.NotEmpty(For(shown, "b"));
        Assert.Empty(For(hidden, "b"));
        Assert.Equal(For(shown, "a").Count(), For(hidden, "a").Count());
    }

    [Fact]
    public void HidingAnInstanceDoesNotRenumberTheOthers()
    {
        // The property the model tree depends on: index 2 is still index 2 with index 0
        // hidden, so a checkbox cannot make the tree address the wrong geometry.
        var instances = new[] { Instance("a", visible: false), Instance("b"), Instance("c") };

        var frame = Build(instances, selected: 2);

        var fill = For(frame, "c").First(d => d.Program == ViewportFrame.MeshProgram);
        Assert.Equal(Highlight.Selected, fill.Uniforms!["uHighlight"]);
        Assert.Empty(For(frame, "a"));
    }

    [Fact]
    public void HidingWorksInEveryEffectiveMode()
    {
        foreach (var style in Enum.GetValues<ViewStyle>())
        {
            Assert.Empty(For(Build([Instance("a", visible: false)], style), "a"));
            Assert.NotEmpty(For(Build([Instance("a")], style), "a"));
        }
        // Translucent is a part mode rather than a style, and it takes its own pass.
        Assert.Empty(For(Build([Instance("a", DisplayMode.Translucent, visible: false)]), "a"));
    }

    [Fact]
    public void HidingEveryInstanceLeavesJustTheBackground()
    {
        var frame = Build([Instance("a", visible: false), Instance("b", visible: false)]);

        var background = Assert.Single(frame.Draws);
        Assert.Equal(ViewportFrame.BackgroundProgram, background.Program);
    }

    // ---- selection and hover on shaded fills ----

    [Fact]
    public void NothingSelectedLeavesEveryFillAtTheNeutralSharedValue()
    {
        var frame = Build([Instance("a"), Instance("b")]);

        Assert.Equal(0f, frame.Shared!["uHighlight"]);
        foreach (var fill in frame.Draws.Where(d => d.Program == ViewportFrame.MeshProgram))
            Assert.False(fill.Uniforms!.ContainsKey("uHighlight"));
    }

    [Fact]
    public void TheSelectedFillCarriesTheSelectionStrengthAndNoOtherDoes()
    {
        var frame = Build([Instance("a"), Instance("b")], selected: 1);

        var a = For(frame, "a").First(d => d.Program == ViewportFrame.MeshProgram);
        var b = For(frame, "b").First(d => d.Program == ViewportFrame.MeshProgram);
        Assert.False(a.Uniforms!.ContainsKey("uHighlight"));
        Assert.Equal(Highlight.Selected, b.Uniforms!["uHighlight"]);
    }

    [Fact]
    public void TheHoveredFillCarriesTheFainterStrength()
    {
        var frame = Build([Instance("a"), Instance("b")], hovered: 0);

        var a = For(frame, "a").First(d => d.Program == ViewportFrame.MeshProgram);
        Assert.Equal(Highlight.Hovered, a.Uniforms!["uHighlight"]);
        Assert.True(Highlight.Hovered < Highlight.Selected);
    }

    [Fact]
    public void AHoveredSelectedInstanceShowsSelectionOnly()
    {
        var frame = Build([Instance("a")], selected: 0, hovered: 0);

        var fill = For(frame, "a").First(d => d.Program == ViewportFrame.MeshProgram);
        Assert.Equal(Highlight.Selected, fill.Uniforms!["uHighlight"]);
    }

    [Fact]
    public void SelectingChangesTheUniformsAndNothingElseAboutTheDrawList()
    {
        // The silhouette must not move: same programs, same geometry, same order, same
        // count. Only what the fragment shader is told changes.
        var plain = Build([Instance("a"), Instance("b")]);
        var selected = Build([Instance("a"), Instance("b")], selected: 0);

        Assert.Equal(plain.Draws.Count, selected.Draws.Count);
        for (int i = 0; i < plain.Draws.Count; i++)
        {
            Assert.Equal(plain.Draws[i].Program, selected.Draws[i].Program);
            Assert.Equal(plain.Draws[i].Geometry, selected.Draws[i].Geometry);
            Assert.Equal(plain.Draws[i].Count, selected.Draws[i].Count);
            Assert.Equal(plain.Draws[i].Blend, selected.Draws[i].Blend);
        }
    }

    [Fact]
    public void AnOutOfRangeSelectionHighlightsNothing()
    {
        var frame = Build([Instance("a")], selected: 7, hovered: -3);

        var fill = For(frame, "a").First(d => d.Program == ViewportFrame.MeshProgram);
        Assert.False(fill.Uniforms!.ContainsKey("uHighlight"));
    }

    // ---- selection on the line overlay ----

    [Fact]
    public void TheSelectedPartsFeatureEdgesDrawInSelectionGold()
    {
        var frame = Build([Instance("a"), Instance("b")], selected: 0);

        var goldEdges = For(frame, "a.edges").Single();
        var plainEdges = For(frame, "b.edges").Single();
        Assert.Equal(
            [Highlight.Selection.R, Highlight.Selection.G, Highlight.Selection.B],
            (float[])goldEdges.Uniforms!["uColor"]);
        Assert.Equal(ViewportFrame.EdgeColor, (float[])plainEdges.Uniforms!["uColor"]);
    }

    [Fact]
    public void HoverDoesNotTintFeatureEdges()
    {
        // The desktop tints the FILL on hover and leaves the overlay dark; a gold overlay
        // under the cursor would read as a selection that has not happened.
        var frame = Build([Instance("a")], hovered: 0);

        Assert.Equal(ViewportFrame.EdgeColor, (float[])For(frame, "a.edges").Single().Uniforms!["uColor"]);
    }

    [Fact]
    public void AWireframePartTakesItsHighlightThroughTheLineColour()
    {
        // A wireframe part has no fill, so uHighlight has nothing to act on: selection
        // and hover must reach it through uColor or not at all.
        var wire = Instance("a", DisplayMode.Wireframe);

        var plain = (float[])For(Build([wire]), "a.wire").Single().Uniforms!["uColor"];
        var selected = (float[])For(Build([wire], selected: 0), "a.wire").Single().Uniforms!["uColor"];
        var hovered = (float[])For(Build([wire], hovered: 0), "a.wire").Single().Uniforms!["uColor"];

        var own = (Palette.Brass.R, Palette.Brass.G, Palette.Brass.B);
        Assert.Equal([own.R, own.G, own.B], plain);
        Assert.Equal([Highlight.Selection.R, Highlight.Selection.G, Highlight.Selection.B], selected);
        var blend = Highlight.LineColor(0, -1, 0, own);
        Assert.Equal([blend.R, blend.G, blend.B], hovered);
        // The hover blend is between the two, which is what makes it read as a hint.
        Assert.NotEqual(plain, hovered);
        Assert.NotEqual(selected, hovered);
    }

    [Fact]
    public void PointSpritesHighlightThroughTheirColourToo()
    {
        var frame = Build([Instance("a")], ViewStyle.Points, selected: 0);

        var points = frame.Draws.Single(d => d.Program == ViewportFrame.PointProgram);
        Assert.Equal(
            [Highlight.Selection.R, Highlight.Selection.G, Highlight.Selection.B],
            (float[])points.Uniforms!["uColor"]);
    }

    // ---- translucency ----

    [Fact]
    public void ASelectedTranslucentPartHighlightsItsBlendedFillAndItsEdges()
    {
        var frame = Build([Instance("a", DisplayMode.Translucent)], selected: 0);

        var fill = frame.Draws.Single(d => d.Program == ViewportFrame.MeshProgram);
        Assert.True(fill.Blend);
        Assert.Equal(ViewportFrame.TranslucentAlpha, fill.Uniforms!["uAlpha"]);
        Assert.Equal(Highlight.Selected, fill.Uniforms["uHighlight"]);
        Assert.Equal(
            [Highlight.Selection.R, Highlight.Selection.G, Highlight.Selection.B],
            (float[])For(frame, "a.edges").Single().Uniforms!["uColor"]);
    }

    [Fact]
    public void TheTranslucentSortStillRunsOverTheVISIBLEInstancesOnly()
    {
        // Sorting a hidden instance into the order would leave a gap in the pass and,
        // worse, make the back-to-front order depend on something not drawn.
        var near = new ViewportInstance(
            "near", Matrix4d.Identity, Palette.Brass, (0, 0, 100), DisplayMode.Translucent);
        var far = new ViewportInstance(
            "far", Matrix4d.Identity, Palette.Steel, (0, 0, -100), DisplayMode.Translucent);
        var hiddenMiddle = new ViewportInstance(
            "mid", Matrix4d.Identity, Palette.Coral, Vector3d.Zero, DisplayMode.Translucent,
            Visible: false);

        var frame = Build([near, hiddenMiddle, far]);

        var fills = frame.Draws
            .Where(d => d.Program == ViewportFrame.MeshProgram)
            .Select(d => d.Geometry)
            .ToArray();
        Assert.Equal(2, fills.Length);
        Assert.DoesNotContain("mid", fills);
    }
}
