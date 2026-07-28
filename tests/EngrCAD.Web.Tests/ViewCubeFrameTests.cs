using EngrCAD.Core;
using EngrCAD.Viewer;
using EngrCAD.Web;
using Xunit;

namespace EngrCAD.Web.Tests;

/// <summary>
/// The view cube in the browser frame, asserted as values. Everything that decides what
/// the cube looks like is the shared <c>ViewCubeMath</c>/<c>ViewCubeGeometry</c> — the
/// same pose table, region maths, palette and hover rule the desktop widget uses — so
/// these tests pin the plumbing: draw order, the depth-clear overlay trick, the
/// sub-viewport rect, and that expectations are READ FROM the shared geometry rather
/// than re-typed.
/// </summary>
public class ViewCubeFrameTests
{
    private static readonly Aabb Bounds = new((-10, -10, 0), (10, 10, 5));

    private static CameraState Camera => ViewportFrame.DefaultCamera(Bounds);

    private static FrameDescription Build(
        ViewportCube? cube, double pixelScale = 1.0,
        IReadOnlyList<SectionPlane>? planes = null) =>
        ViewportFrame.Build([], Camera, Bounds, aspect: 1.6, pixelScale: pixelScale,
            sectionPlanes: planes, cube: cube);

    private static List<DrawCall> CubeDraws(FrameDescription frame) =>
        frame.Draws.Where(d => d.Geometry?.StartsWith("@cube.") == true).ToList();

    [Fact]
    public void CubeDrawsLastIntoItsOwnCornerViewport()
    {
        var frame = Build(new ViewportCube(800, 600));
        var draws = CubeDraws(frame);

        // 6 face fills + edges + labels, and they are the LAST draws of the frame —
        // the cube is window chrome sitting on top of everything, the desktop rule.
        Assert.Equal(ViewCubeGeometry.Faces.Count + 2, draws.Count);
        Assert.Equal(draws, frame.Draws.TakeLast(draws.Count).ToList());

        // Region rect: the desktop's DIP maths at pixel scale 1.
        int size = (int)ViewCubeMath.RegionSizeDip;
        int margin = (int)ViewCubeMath.RegionMarginDip;
        int[] expected = [800 - size - margin, 600 - size - margin, size, size];
        Assert.All(draws, call => Assert.Equal(expected, call.Viewport));

        // The depth clear that makes it an overlay: exactly once, before the first draw.
        Assert.True(draws[0].ClearDepth);
        Assert.All(draws.Skip(1), call => Assert.False(call.ClearDepth));
    }

    [Fact]
    public void FillsDrawPerFaceThroughTheLineProgramInTableOrder()
    {
        var draws = CubeDraws(Build(new ViewportCube(800, 600)));

        for (int face = 0; face < ViewCubeGeometry.Faces.Count; face++)
        {
            var call = draws[face];
            Assert.Equal(ViewportFrame.LineProgram, call.Program);
            Assert.Equal(ViewportFrame.CubeFillsKey, call.Geometry);
            Assert.Equal("triangles", call.Mode);
            Assert.Equal(face * ViewCubeGeometry.VerticesPerFace, call.First);
            Assert.Equal(ViewCubeGeometry.VerticesPerFace, call.Count);
            // Fills pushed back so edges and labels win the depth test — the same
            // trick as the scene's feature-edge overlay.
            Assert.Equal([1f, 1f], call.PolygonOffset!);
            var c = ViewCubeGeometry.Faces[face].Color;
            Assert.Equal([c.R, c.G, c.B], (float[])call.Uniforms!["uColor"]);
        }

        // Edges then labels, in the shared palette and at the shared vertex counts.
        var edges = draws[^2];
        Assert.Equal(ViewportFrame.CubeEdgesKey, edges.Geometry);
        Assert.Equal(ViewCubeGeometry.BuildEdgeVertices().Length / 3, edges.Count);
        var e = ViewCubeGeometry.EdgeColor;
        Assert.Equal([e.R, e.G, e.B], (float[])edges.Uniforms!["uColor"]);

        var labels = draws[^1];
        Assert.Equal(ViewportFrame.CubeLabelsKey, labels.Geometry);
        Assert.Equal(ViewCubeGeometry.BuildLabelVertices().Length / 3, labels.Count);
        var l = ViewCubeGeometry.LabelColor;
        Assert.Equal([l.R, l.G, l.B], (float[])labels.Uniforms!["uColor"]);
    }

    [Fact]
    public void CubeUsesItsOwnOrthoMiniProjection()
    {
        var camera = Camera;
        var draws = CubeDraws(Build(new ViewportCube(800, 600)));

        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, ViewCubeMath.EyeDistance, Vector3d.Zero);
        var view = new float[16];
        CameraMath.WriteColumnMajor(CameraMath.LookAt(eye, Vector3d.Zero, Vector3d.UnitZ), view);
        var projection = new float[16];
        CameraMath.WriteColumnMajor(
            CameraMath.Orthographic(ViewCubeMath.OrthoHalfExtent, 1, 0.5, 8), projection);

        Assert.All(draws, call =>
        {
            Assert.Equal(view, (float[])call.Uniforms!["uView"]);
            Assert.Equal(projection, (float[])call.Uniforms["uProj"]);
        });
    }

    [Fact]
    public void HoverBrightensExactlyTheContributingFaces()
    {
        // An edge direction contributes to TWO faces; the expectation is read FROM the
        // shared hover rule (Brightened + the face-normal dot), never re-typed.
        var hover = new Vector3d(1, -1, 0);   // front-right edge
        var draws = CubeDraws(Build(new ViewportCube(800, 600, hover)));

        for (int face = 0; face < ViewCubeGeometry.Faces.Count; face++)
        {
            var entry = ViewCubeGeometry.Faces[face];
            var expected = hover.Dot(entry.Normal) > 0.5
                ? ViewCubeGeometry.Brightened(entry.Color)
                : entry.Color;
            Assert.Equal([expected.R, expected.G, expected.B],
                (float[])draws[face].Uniforms!["uColor"]);
        }
    }

    [Fact]
    public void PixelScaleSizesTheRegionInFramebufferPixels()
    {
        var draws = CubeDraws(Build(new ViewportCube(800, 600), pixelScale: 2.0));

        int size = (int)(ViewCubeMath.RegionSizeDip * 2);
        int margin = (int)(ViewCubeMath.RegionMarginDip * 2);
        Assert.Equal([1600 - size - margin, 1200 - size - margin, size, size],
            draws[0].Viewport);
    }

    [Fact]
    public void TooSmallCanvasOmitsTheCube()
    {
        // The desktop's guard: no room for the widget means no widget, not a clipped one.
        Assert.Empty(CubeDraws(Build(new ViewportCube(100, 100))));
    }

    [Fact]
    public void CubeIsNeverSectionClipped()
    {
        var draws = CubeDraws(Build(
            new ViewportCube(800, 600), planes: [SectionPlane.On(SectionAxis.Z, 1)]));

        Assert.NotEmpty(draws);
        Assert.All(draws, call => Assert.Equal(0f, call.Uniforms!["uSectionEnabled"]));
    }

    [Fact]
    public void NoCubeMeansNoCubeDraws() =>
        Assert.Empty(CubeDraws(Build(cube: null)));
}
