using EngrCAD.Viewer;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The screenshot tool's option surface: multi-plane sections with a combine mode and
/// the explicit camera. Validation is asserted headlessly (arguments are checked
/// before the GL probe, deliberately); the rendering tests join the offscreen-gl
/// collection and skip without a context.
/// </summary>
public class ScreenshotOptionValidationTests
{
    private static SceneTools Tools() => new(new SceneSession(TestScenes.Basic()));

    private static string ErrorText(CallToolResult result)
    {
        Assert.True(result.IsError == true, "expected an error result");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    [Fact]
    public void NamedView_and_explicit_camera_are_mutually_exclusive()
    {
        string error = ErrorText(Tools().Screenshot(view: "front", cameraYaw: 30));
        Assert.Contains("either a named view or explicit camera", error);
    }

    [Fact]
    public void CameraEye_excludes_the_pose_parameters()
    {
        string error = ErrorText(Tools().Screenshot(cameraEye: [10, 10, 10], cameraDistance: 5));
        Assert.Contains("cameraEye already fixes the pose", error);
    }

    [Fact]
    public void SectionPlanes_and_axis_offset_are_mutually_exclusive()
    {
        string error = ErrorText(Tools().Screenshot(
            sectionAxis: "z", sectionOffset: 1, sectionPlanes: [[0, 0, 1, 1]]));
        Assert.Contains("not both", error);
    }

    [Fact]
    public void SectionPlanes_are_validated_row_by_row()
    {
        var tools = Tools();
        Assert.Contains("[nx, ny, nz, offset]",
            ErrorText(tools.Screenshot(sectionPlanes: [[0, 0, 1]])));
        Assert.Contains("non-zero",
            ErrorText(tools.Screenshot(sectionPlanes: [[0, 0, 0, 1]])));
        Assert.Contains("At most 4",
            ErrorText(tools.Screenshot(sectionPlanes:
                [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [1, 1, 0, 0], [0, 1, 1, 0]])));
    }

    [Fact]
    public void SectionCombine_is_validated_and_needs_planes()
    {
        var tools = Tools();
        Assert.Contains("intersection or union",
            ErrorText(tools.Screenshot(sectionPlanes: [[0, 0, 1, 1]], sectionCombine: "quarter")));
        Assert.Contains("needs sectionPlanes",
            ErrorText(tools.Screenshot(sectionCombine: "union")));
    }

    [Fact]
    public void Export_validates_the_png_size()
    {
        string error = ErrorText(Tools().Export("out.png", width: 8, height: 800));
        Assert.Contains("between 16 and 4096", error);
    }
}

[Collection("offscreen-gl")]
public class ScreenshotOptionRenderTests
{
    private static SceneTools Tools() => new(new SceneSession(TestScenes.Basic()));

    private static byte[] Png(CallToolResult result)
    {
        Assert.False(result.IsError == true,
            $"expected an image, got: {string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text))}");
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        return image.Data.ToArray();
    }

    [SkippableFact]
    public void Quarter_cut_union_cut_and_uncut_all_render_differently()
    {
        Skip.IfNot(EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {OffscreenRenderer.UnavailableReason}");
        var tools = Tools();

        double[][] planes = [[1, 0, 0, 0], [0, 1, 0, 0]];
        byte[] uncut = Png(tools.Screenshot(width: 320, height: 240));
        byte[] quarter = Png(tools.Screenshot(width: 320, height: 240, sectionPlanes: planes));
        byte[] union = Png(tools.Screenshot(width: 320, height: 240, sectionPlanes: planes,
            sectionCombine: "union"));

        Assert.NotEqual(uncut, quarter);     // the quarter cut removes one corner
        Assert.NotEqual(uncut, union);       // the union cut removes three
        Assert.NotEqual(quarter, union);     // and the two combine rules disagree
    }

    [SkippableFact]
    public void One_general_plane_matches_the_axis_offset_spelling_exactly()
    {
        Skip.IfNot(EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {OffscreenRenderer.UnavailableReason}");
        var tools = Tools();

        // [0,0,1,offset] IS SectionPlane.On(z, offset); with one plane the combine
        // rules coincide, so the render must be byte-identical.
        byte[] axis = Png(tools.Screenshot(width: 320, height: 240, sectionAxis: "z", sectionOffset: 3));
        byte[] plane = Png(tools.Screenshot(width: 320, height: 240, sectionPlanes: [[0, 0, 1, 3]]));
        Assert.Equal(axis, plane);
    }

    [SkippableFact]
    public void Explicit_camera_poses_drive_the_render()
    {
        Skip.IfNot(EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {OffscreenRenderer.UnavailableReason}");
        var tools = Tools();

        byte[] fromX = Png(tools.Screenshot(width: 320, height: 240, cameraYaw: 0, cameraPitch: 10));
        byte[] fromY = Png(tools.Screenshot(width: 320, height: 240, cameraYaw: 90, cameraPitch: 10));
        Assert.NotEqual(fromX, fromY);       // yaw provably reaches the camera

        byte[] fromEye = Png(tools.Screenshot(width: 320, height: 240,
            cameraEye: [40, -40, 30], cameraTarget: [0, 0, 3]));
        Assert.NotEmpty(fromEye);
        Assert.NotEqual(fromX, fromEye);
    }
}
