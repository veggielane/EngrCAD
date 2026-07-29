using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The capture completion an RPC caller awaits — the half of
/// <see cref="ViewportControl.SaveScreenshot"/> that does NOT need a GL context.
///
/// <para><b>What is being pinned is an ORDER, and it is the whole point of the feature.</b>
/// The render pass reads pixels back and then hands them to a worker to encode and write,
/// so a completion fired inside the render pass — the shape the backlog entry proposed —
/// would hand a caller a path to a file that does not exist yet. It fires from the write
/// instead, immediately after <c>File.WriteAllBytes</c> returns, which is what lets the
/// MCP bridge read the bytes back and answer with an image rather than a promise.</para>
///
/// <para>The status callback is deliberately not the signal: it is a BROADCAST carrying
/// prose for successes and failures alike, so a caller listening on it cannot tell its own
/// capture from the toolbar button's or from a second concurrent request's. That is the
/// argument, and it is why the completion is per-request.</para>
/// </summary>
public class ScreenshotCaptureTests
{
    private static string TempPng() =>
        Path.Combine(Path.GetTempPath(), $"engrcad-capture-{Guid.NewGuid():N}.png");

    [Fact]
    public async Task A_capture_completes_only_once_its_bytes_are_on_disk()
    {
        string path = TempPng();
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new List<string>();

        var writing = ViewportControl.WriteCapture(
            4, 4, new byte[4 * 4 * 4],
            new ViewportControl.PendingScreenshot(path, completion), reported.Add);

        // Resume on the COMPLETION, not on the write task: the file must already be there
        // at the instant the awaiter is released, which is the claim a caller relies on.
        string answered = await completion.Task;
        Assert.Equal(path, answered);
        Assert.True(File.Exists(answered), "the completion fired before the PNG was written");
        Assert.True(new FileInfo(answered).Length > 0);

        await writing;
        Assert.Contains(reported, m => m.Contains("screenshot saved", StringComparison.Ordinal));
        File.Delete(path);
    }

    [Fact]
    public async Task A_capture_that_cannot_be_written_faults_rather_than_hanging()
    {
        // A path UNDER a regular file: creating its directory is impossible, so the write
        // throws where a caller would otherwise wait for a frame that already happened.
        string blocker = TempPng();
        File.WriteAllText(blocker, "not a directory");
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new List<string>();

        await ViewportControl.WriteCapture(
            2, 2, new byte[2 * 2 * 4],
            new ViewportControl.PendingScreenshot(Path.Combine(blocker, "frame.png"), completion),
            reported.Add);

        await Assert.ThrowsAnyAsync<Exception>(() => completion.Task);
        Assert.Contains(reported, m => m.Contains("screenshot failed", StringComparison.Ordinal));
        File.Delete(blocker);
    }

    [Fact]
    public async Task A_fire_and_forget_capture_still_writes_and_reports()
    {
        // SaveScreenshot's shape — no completion at all. The toolbar's Capture button must
        // be untouched by the awaitable path existing beside it.
        string path = TempPng();
        var reported = new List<string>();

        await ViewportControl.WriteCapture(
            4, 4, new byte[4 * 4 * 4],
            new ViewportControl.PendingScreenshot(path, Completion: null), reported.Add);

        Assert.True(File.Exists(path));
        Assert.Contains(reported, m => m.Contains(path, StringComparison.Ordinal));
        File.Delete(path);
    }
}
