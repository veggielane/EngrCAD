using Xunit;

namespace EngrCAD.Core.Tests;

public class ProgressCancelTests
{
    [Fact]
    public void FunctionSource_IsSticky()
    {
        bool flag = false;
        var progress = new ProgressCancel(() => flag);
        Assert.False(progress.CancelRequested);

        flag = true;
        Assert.True(progress.CancelRequested);

        flag = false; // source resets, but the observation is sticky
        Assert.True(progress.CancelRequested);
        Assert.Throws<OperationCanceledException>(progress.ThrowIfCancelled);
    }

    [Fact]
    public void TokenSource_CancelsAndThrows()
    {
        using var cts = new CancellationTokenSource();
        var progress = new ProgressCancel(cts.Token);
        Assert.False(progress.CancelRequested);
        progress.ThrowIfCancelled(); // no-op

        cts.Cancel();
        Assert.True(progress.CancelRequested);
        Assert.Throws<OperationCanceledException>(progress.ThrowIfCancelled);
    }

    [Fact]
    public void Report_ClampsAndForwards()
    {
        var reported = new List<double>();
        var progress = new ProgressCancel(reported.Add);

        progress.Report(0.5);
        progress.Report(-1);
        progress.Report(2);
        progress.Report(double.NaN); // ignored

        Assert.Equal([0.5, 0.0, 1.0], reported);
        Assert.False(progress.CancelRequested); // progress-only never cancels
    }
}
