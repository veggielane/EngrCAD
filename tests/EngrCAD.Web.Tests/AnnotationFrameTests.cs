using EngrCAD.Core;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// The annotation overlay in the browser frame, asserted as values. What a dimension
/// LOOKS like is the shared <c>AnnotationGeometry</c> (its segments are unit-tested in
/// EngrCAD.Viewer.Tests); these tests pin the frame plumbing — depth-off, the shared
/// colour, never section-clipped, and the desktop's pass position.
/// </summary>
public class AnnotationFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static FrameDescription Build(
        ViewportAnnotations? annotations,
        IReadOnlyList<SectionPlane>? planes = null,
        ViewportCube? cube = null,
        AnnotationDepth depth = AnnotationDepth.AlwaysOnTop) =>
        ViewportFrame.Build([], ViewportFrame.DefaultCamera(Bounds), Bounds, aspect: 1.6,
            sectionPlanes: planes, cube: cube, annotations: annotations, annotationDepth: depth);

    [Fact]
    public void OverlayDrawsDepthOffInTheSharedColour()
    {
        var frame = Build(new ViewportAnnotations("@annotations", 40));

        var draw = Assert.Single(frame.Draws, d => d.Geometry == "@annotations");
        Assert.Equal(ViewportFrame.LineProgram, draw.Program);
        Assert.Equal(40, draw.Count);
        // Always-on-top v1: dimensions must read over the model from any angle.
        Assert.False(draw.DepthTest);
        var c = AnnotationGeometry.Color;
        Assert.Equal([c.R, c.G, c.B], (float[])draw.Uniforms!["uColor"]);
        // Neutral section state says nothing, as everywhere else.
        Assert.DoesNotContain("uSectionEnabled", draw.Uniforms.Keys);
    }

    [Fact]
    public void OverlayIsNeverSectionClipped()
    {
        var frame = Build(new ViewportAnnotations("@annotations", 40),
            planes: [SectionPlane.On(SectionAxis.Z, 1)]);

        var draw = Assert.Single(frame.Draws, d => d.Geometry == "@annotations");
        Assert.Equal(0f, draw.Uniforms!["uSectionEnabled"]);
    }

    [Fact]
    public void OverlayDrawsAfterTheSceneAndBeforeTheCube()
    {
        var frame = Build(new ViewportAnnotations("@annotations", 40),
            cube: new ViewportCube(800, 600));

        int annotationAt = IndexOf(frame, "@annotations");
        int firstCubeAt = frame.Draws.ToList()
            .FindIndex(d => d.Geometry?.StartsWith("@cube.") == true);
        Assert.True(annotationAt >= 0 && firstCubeAt > annotationAt,
            "annotations must draw before the view cube (the desktop pass order)");
    }

    [Fact]
    public void EmptyOverlayContributesNothing()
    {
        Assert.DoesNotContain(Build(new ViewportAnnotations("@annotations", 0)).Draws,
            d => d.Geometry == "@annotations");
        Assert.DoesNotContain(Build(null).Draws, d => d.Geometry == "@annotations");
    }

    // ---- occlusion-aware mode (AnnotationDepth.Occluded) ----

    /// <summary>
    /// The three draws and their depth functions: the SAME upload's line-work range at
    /// LEQUAL then GREATER, then the text range depth-off. Asserted as values because
    /// that is exactly what a screenshot cannot show — LEQUAL and GREATER partition the
    /// fragments only if both are present with the same range, and a copy of the rule
    /// would agree with a broken implementation as happily as with a correct one.
    /// </summary>
    [Fact]
    public void OccludedDrawsTheLineWorkTwiceAndTheTextOnce()
    {
        var frame = Build(new ViewportAnnotations("@annotations", 40, 24),
            depth: AnnotationDepth.Occluded);

        var draws = frame.Draws.Where(d => d.Geometry == "@annotations").ToList();
        Assert.Equal(3, draws.Count);

        var visible = draws[0];
        Assert.True(visible.DepthTest);
        Assert.Equal("lequal", visible.DepthFunc);
        Assert.Equal((0, 24), (visible.First, visible.Count));
        Assert.Equal(Rgb(AnnotationGeometry.Color), (float[])visible.Uniforms!["uColor"]);

        var hidden = draws[1];
        Assert.True(hidden.DepthTest);
        Assert.Equal("greater", hidden.DepthFunc);
        // The SAME range: the depth buffer, not a second geometry, makes the split.
        Assert.Equal((0, 24), (hidden.First, hidden.Count));
        Assert.Equal(Rgb(AnnotationGeometry.HiddenColor), (float[])hidden.Uniforms!["uColor"]);

        // The value is exempt: full strength, no depth test, whichever side it sits on.
        var text = draws[2];
        Assert.False(text.DepthTest);
        Assert.Null(text.DepthFunc);
        Assert.Equal((24, 16), (text.First, text.Count));
        Assert.Equal(Rgb(AnnotationGeometry.Color), (float[])text.Uniforms!["uColor"]);

        // None of them may write depth: the cube and the legend draw after the overlay.
        Assert.All(draws, d => Assert.False(d.DepthWrite));
    }

    /// <summary>The default mode says nothing about the depth comparison, so every other
    /// draw's assumption that it is GL's own LESS is undisturbed.</summary>
    [Fact]
    public void AlwaysOnTopNamesNoDepthFunction()
    {
        var draw = Assert.Single(Build(new ViewportAnnotations("@annotations", 40, 24)).Draws,
            d => d.Geometry == "@annotations");
        Assert.Null(draw.DepthFunc);
        Assert.False(draw.DepthTest);
        // The whole upload, undivided — the line-work split is Occluded's business only.
        Assert.Equal((0, 40), (draw.First, draw.Count));
    }

    /// <summary>An overlay that is all text (a lone leader note whose leader happens to
    /// be empty is impossible, but a caller passing 0 is not) draws only the text range —
    /// no zero-length line-work draws, which GL would accept and which would make the
    /// draw list depend on the DATA rather than on the mode.</summary>
    [Fact]
    public void OccludedEmitsNoEmptyRanges()
    {
        var allLineWork = Build(new ViewportAnnotations("@annotations", 40, 40),
            depth: AnnotationDepth.Occluded).Draws.Where(d => d.Geometry == "@annotations").ToList();
        Assert.Equal(2, allLineWork.Count);
        Assert.All(allLineWork, d => Assert.True(d.DepthTest));

        var allText = Build(new ViewportAnnotations("@annotations", 40, 0),
            depth: AnnotationDepth.Occluded).Draws.Where(d => d.Geometry == "@annotations").ToList();
        var only = Assert.Single(allText);
        Assert.False(only.DepthTest);
        Assert.Equal((0, 40), (only.First, only.Count));
    }

    /// <summary>Occluded is still documentation: none of its three draws clips.</summary>
    [Fact]
    public void OccludedIsNeverSectionClipped()
    {
        var draws = Build(new ViewportAnnotations("@annotations", 40, 24),
            planes: [SectionPlane.On(SectionAxis.Z, 1)],
            depth: AnnotationDepth.Occluded).Draws.Where(d => d.Geometry == "@annotations");
        Assert.All(draws, d => Assert.Equal(0f, d.Uniforms!["uSectionEnabled"]));
    }

    private static float[] Rgb((float R, float G, float B) c) => [c.R, c.G, c.B];

    private static int IndexOf(FrameDescription frame, string geometry)
    {
        for (int i = 0; i < frame.Draws.Count; i++)
        {
            if (frame.Draws[i].Geometry == geometry)
                return i;
        }
        return -1;
    }
}
