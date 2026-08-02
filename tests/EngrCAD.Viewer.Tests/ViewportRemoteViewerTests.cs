using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// <see cref="ViewportRemoteViewer"/> against a <b>real</b> <see cref="ViewportControl"/> —
/// the leg todo.md recorded as needing a windowed manual pass because the class "takes a
/// concrete ViewportControl, so there is nothing to substitute".
///
/// <para><b>The filed diagnosis had the conclusion right and the reason wrong.</b> Nothing
/// needs substituting: a <c>ViewportControl</c> constructs perfectly well with no Avalonia
/// application, no window and no GL context — and one that will never be rendered IS the
/// fixture the screenshot deadline exists for, since "no frame arrives" is exactly its
/// state. The only genuinely windowed part left is a real GL render pass consuming the
/// armed capture, which <c>WindowedRpcTests</c> in EngrCAD.Mcp.Tests drives against a live
/// process.</para>
///
/// <para><b>What is pinned here is the ARM/WAIT SPLIT.</b> Arming posts a render request
/// and must happen on the UI thread; WAITING for the frame must NOT, because blocking the
/// dispatcher is precisely how the frame would fail to arrive — a deadlock that looks
/// exactly like a slow window. The injected invoker makes that observable: it is called
/// once, it has returned before the capture resolves, and the whole capture costs exactly
/// one UI hop.</para>
/// </summary>
public class ViewportRemoteViewerTests
{
    private static string TempPng() =>
        Path.Combine(Path.GetTempPath(), $"engrcad-rpc-{Guid.NewGuid():N}.png");

    /// <summary>A synchronous stand-in for <c>Dispatcher.UIThread.InvokeAsync</c> that
    /// records how often it ran and whether control is currently inside it.</summary>
    private sealed class RecordingInvoker
    {
        public int Calls;
        public bool Inside;

        public Task<object?> Invoke(Func<object?> work)
        {
            Calls++;
            Inside = true;
            try
            {
                return Task.FromResult(work());
            }
            finally
            {
                Inside = false;
            }
        }
    }

    [Fact]
    public async Task Arming_happens_on_the_UI_thread_and_the_wait_does_not()
    {
        var control = new ViewportControl();
        var invoker = new RecordingInvoker();
        var viewer = new ViewportRemoteViewer(control, invoker.Invoke);
        string path = TempPng();

        var screenshot = viewer.ScreenshotAsync(path);

        // The UI hop has already happened and RETURNED, while the capture is still
        // outstanding. If waiting lived inside the hop, the invoker would still be on the
        // stack here (and, with a real dispatcher, the render pass would never run).
        Assert.Equal(1, invoker.Calls);
        Assert.False(invoker.Inside);
        Assert.False(screenshot.IsCompleted);

        // Play the render pass: claim the armed capture and write it. (The GL half —
        // glReadPixels — is the only piece a window is needed for.)
        var capture = control.TakePendingScreenshot();
        Assert.NotNull(capture);
        Assert.Equal(path, capture!.Path);
        await ViewportControl.WriteCapture(4, 4, new byte[4 * 4 * 4], capture, _ => { });

        Assert.Equal(path, await screenshot);
        Assert.True(File.Exists(path), "the RPC caller was released before the PNG existed");
        Assert.Equal(1, invoker.Calls);         // one hop for the whole capture, not one per stage
        File.Delete(path);
    }

    [Fact]
    public void Readiness_is_false_until_the_render_pass_adopts_instances()
    {
        // The measured startup race: the RPC port is announced from OnViewportReady
        // while instances handed to SetInstances wait for the render pass to swap them
        // in. A control that never renders IS that gap, held open — so the headless
        // half of the fixture pins the FALSE readings, and WindowedRpcTests (a live
        // render pass) covers ready flipping true.
        var control = new ViewportControl();
        Assert.False(control.InstancesDisplayed);   // nothing has ever been displayed

        control.SetInstances([], frame: false);
        Assert.False(control.InstancesDisplayed);   // queued, not adopted — still the gap
    }

    [Fact]
    public void A_claimed_capture_is_claimed_exactly_once()
    {
        // The render pass runs on every frame; a capture that could be claimed twice would
        // be written twice (and the second write would find the completion already set).
        var control = new ViewportControl();
        string path = TempPng();
        _ = control.CaptureScreenshotAsync(path);

        Assert.Equal(path, control.TakePendingScreenshot()?.Path);
        Assert.Null(control.TakePendingScreenshot());
    }

    [Fact]
    public async Task A_superseded_capture_fails_by_name_rather_than_waiting_forever()
    {
        var control = new ViewportControl();
        string first = TempPng(), second = TempPng();

        var superseded = control.CaptureScreenshotAsync(first);
        var winner = control.CaptureScreenshotAsync(second);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => superseded);
        Assert.Contains("superseded", failure.Message, StringComparison.Ordinal);
        Assert.False(winner.IsCompleted);
        Assert.Equal(second, control.TakePendingScreenshot()?.Path);
    }

    [Fact]
    public async Task A_window_that_never_renders_refuses_by_deadline_and_writes_nothing()
    {
        // A control with no window will never run a render pass, which is the state the
        // deadline exists for: a minimised or occluded window can sit there indefinitely,
        // and a hung RPC connection is a worse answer than an honest refusal.
        var control = new ViewportControl();
        var viewer = new ViewportRemoteViewer(control, work => Task.FromResult(work()))
        {
            ScreenshotTimeout = TimeSpan.FromMilliseconds(150),
        };
        string path = TempPng();

        var refusal = await Assert.ThrowsAsync<RemoteMethodException>(() => viewer.ScreenshotAsync(path));

        Assert.Contains("no frame", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(path, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("occluded", refusal.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path), "a refused capture must not leave a file behind");
        // -32000: a server-side failure, not a bad argument — the request was well formed.
        Assert.Equal(-32000, refusal.Code);
    }

    [Fact]
    public async Task A_capture_that_cannot_be_written_surfaces_its_own_failure_not_a_timeout()
    {
        // WaitAsync rather than a WhenAny race is what makes this distinguishable: the
        // write's exception propagates, so "the path is unwritable" never masquerades as
        // "the window did not render".
        string blocker = TempPng();
        File.WriteAllText(blocker, "not a directory");
        var control = new ViewportControl();
        var viewer = new ViewportRemoteViewer(control, work => Task.FromResult(work()))
        {
            ScreenshotTimeout = TimeSpan.FromSeconds(30),
        };

        var screenshot = viewer.ScreenshotAsync(Path.Combine(blocker, "frame.png"));
        var capture = control.TakePendingScreenshot();
        Assert.NotNull(capture);
        await ViewportControl.WriteCapture(2, 2, new byte[2 * 2 * 4], capture!, _ => { });

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => screenshot);
        Assert.IsNotType<RemoteMethodException>(failure);
        File.Delete(blocker);
    }
}
