using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>GL-touching builder paths: <c>--render</c> honors the configured render
/// size. Shares the "offscreen-gl" collection (no concurrent EGL contexts).</summary>
[Collection("offscreen-gl")]
public class BuilderRenderTests
{
    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    private static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // IHDR immediately follows the 8-byte signature; width/height are big-endian.
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return (width, height);
    }

    private static Func<Scene> BoxScene => () =>
    {
        var scene = new Scene();
        scene.Add(new Part("box", Shape.Box(2, 2, 2)));
        return scene;
    };

    private static byte[] RenderRun(Func<EngrCadBuilder, EngrCadBuilder> configure, params string[] extraArgs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"engrcad-builder-flow-{Guid.NewGuid():N}.png");
        try
        {
            var builder = configure(EngrCad.Configure().WithRenderSize(160, 120).WithLog(_ => { }));
            Assert.Equal(0, builder.Run(["--render", path, .. extraArgs], BoxScene));
            return File.ReadAllBytes(path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [SkippableFact]
    public void RenderRun_HonorsStyleAndSectionOptions()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // Options flow: WithViewStyle/WithSection reach the offscreen pass — each
        // configuration renders visibly differently from the defaults; and the CLI
        // switches override the builder's values (wireframe via --render-style over a
        // configured Points style matches wireframe configured directly).
        var plain = RenderRun(b => b);
        var wire = RenderRun(b => b.WithViewStyle(ViewStyle.Wireframe));
        var sectioned = RenderRun(b => b.WithSection(SectionAxis.Z, 0.0)); // box spans z [-1, 1]
        var cliWire = RenderRun(b => b.WithViewStyle(ViewStyle.Points), "--render-style", "wireframe");
        var cliSection = RenderRun(b => b, "--section", "z", "0");

        Assert.NotEqual(plain, wire);
        Assert.NotEqual(plain, sectioned);
        Assert.Equal(wire, cliWire);
        Assert.Equal(sectioned, cliSection);
    }

    [SkippableFact]
    public void RenderRun_UsesConfiguredRenderSize()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var messages = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"engrcad-builder-render-{Guid.NewGuid():N}.png");
        try
        {
            int code = EngrCad.Configure()
                .WithRenderSize(200, 150)
                .WithLog(messages.Add)
                .Run(["--render", path], () =>
                {
                    var scene = new Scene();
                    scene.Add(new Part("box", Shape.Box(2, 1, 1)));
                    return scene;
                });
            Assert.Equal(0, code);
            Assert.Equal((200, 150), PngSize(path));
            Assert.Contains(messages, m => m.Contains("wrote"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
