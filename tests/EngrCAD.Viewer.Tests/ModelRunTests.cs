using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The <c>$t</c> half of <see cref="EngrCad.Run(string[], Func{double, Scene}, string)"/>:
/// <c>--animate</c> bakes, and every other verb answers about ONE instant chosen by
/// <c>--t</c>. Headless — no Avalonia lifetime is started.
/// </summary>
[Collection("offscreen-gl")]
public class ModelRunTests
{
    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"engrcad-model-{Guid.NewGuid():N}{extension}");

    /// <summary>A box whose height follows t, so which instant a verb answered about is
    /// readable off the geometry rather than taken on trust.</summary>
    private static Scene GrowingBox(double t)
    {
        var scene = new Scene();
        scene.Add(new Part("body", Shape.Box(20, 20, 5 + 40 * t)));
        return scene;
    }

    [SkippableFact]
    public void AnimateWritesAnApng()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);
        string path = TempPath(".png");
        try
        {
            Assert.Equal(0, EngrCad.Run(["--animate", path, "--frames", "3"], GrowingBox));
            Assert.Contains("acTL", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void AnimateWithNoExtensionWritesAFrameSequenceDirectory()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);
        string dir = TempPath("");
        try
        {
            Assert.Equal(0, EngrCad.Run(["--animate", dir, "--frames", "2"], GrowingBox));
            Assert.True(File.Exists(Path.Combine(dir, "frame-0000.png")));
            Assert.True(File.Exists(Path.Combine(dir, "frame-0001.png")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EveryOtherVerbAnswersAboutTheInstantTNames()
    {
        string path = TempPath(".obj");
        try
        {
            // t = 1 is the tall box; the export is the model AT that instant, so the
            // vertex range says which one was asked for.
            Assert.Equal(0, EngrCad.Run(["--export", path, "--t", "1"], GrowingBox));
            var zs = File.ReadAllLines(path)
                .Where(l => l.StartsWith("v "))
                .Select(l => double.Parse(l.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            Assert.Equal(45, zs.Max() - zs.Min(), 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithNoTTheInstantIsZero()
    {
        string path = TempPath(".obj");
        try
        {
            Assert.Equal(0, EngrCad.Run(["--export", path], GrowingBox));
            var zs = File.ReadAllLines(path)
                .Where(l => l.StartsWith("v "))
                .Select(l => double.Parse(l.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            Assert.Equal(5, zs.Max() - zs.Min(), 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--t", "1.5")]     // outside [0, 1]
    [InlineData("--t", "nope")]
    [InlineData("--frames", "1")]  // a clip needs two
    [InlineData("--frames", "601")]
    public void ABadInstantOrFrameCountIsAUsageError(string flag, string value) =>
        Assert.Equal(2, EngrCad.Run(["--animate", "out.png", flag, value], GrowingBox));

    [Fact]
    public void AnUnsupportedAnimationFormatIsRefused() =>
        Assert.Equal(2, EngrCad.Run(["--animate", TempPath(".mp4")], GrowingBox));

    [Fact]
    public void AnimateWithNoPathIsAUsageError() =>
        Assert.Equal(2, EngrCad.Run(["--animate"], GrowingBox));
}
