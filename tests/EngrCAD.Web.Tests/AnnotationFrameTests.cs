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
        ViewportCube? cube = null) =>
        ViewportFrame.Build([], ViewportFrame.DefaultCamera(Bounds), Bounds, aspect: 1.6,
            sectionPlanes: planes, cube: cube, annotations: annotations);

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
