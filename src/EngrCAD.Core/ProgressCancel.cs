namespace EngrCAD.Core;

/// <summary>
/// Cooperative cancellation + progress reporting for long-running kernel operations
/// (polygonization, decimation, bakes). Operations accept a
/// <c>ProgressCancel? progress = null</c> parameter, poll it at coarse checkpoints, and
/// surface cancellation by throwing <see cref="OperationCanceledException"/> (partial
/// results are discarded — kernel algorithms never return half-built geometry).
/// A null argument costs nothing beyond a null check.
/// </summary>
/// <remarks>
/// The cancel source may be a <see cref="CancellationToken"/> or any <c>Func&lt;bool&gt;</c>
/// (e.g. a UI flag); the result is sticky — once observed cancelled, it stays cancelled.
/// Progress is reported as a fraction in [0, 1]; callbacks may arrive from worker
/// threads when the operation runs parallel phases, and fractions are coarse phase
/// estimates, not precise work counts. Modeled on geometry3Sharp's ProgressCancel,
/// rebuilt on the platform's CancellationToken.
/// </remarks>
public sealed class ProgressCancel
{
    private readonly CancellationToken _token;
    private readonly Func<bool>? _cancelled;
    private readonly Action<double>? _progress;
    private volatile bool _wasCancelled;

    /// <summary>Cancellation driven by a <see cref="CancellationToken"/>, with an optional progress callback.</summary>
    public ProgressCancel(CancellationToken token, Action<double>? progress = null)
    {
        _token = token;
        _progress = progress;
    }

    /// <summary>Cancellation driven by a polling function returning true to cancel, with an optional progress callback.</summary>
    public ProgressCancel(Func<bool> cancelled, Action<double>? progress = null)
    {
        _cancelled = cancelled ?? throw new ArgumentNullException(nameof(cancelled));
        _progress = progress;
    }

    /// <summary>Progress reporting only; never cancels.</summary>
    public ProgressCancel(Action<double> progress)
    {
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
    }

    /// <summary>
    /// True once cancellation has been requested. Sticky: keeps returning true after the
    /// first observation even if the underlying source resets.
    /// </summary>
    public bool CancelRequested
    {
        get
        {
            if (_wasCancelled)
                return true;
            if (_token.IsCancellationRequested || (_cancelled?.Invoke() ?? false))
                _wasCancelled = true;
            return _wasCancelled;
        }
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> if cancellation has been requested.</summary>
    public void ThrowIfCancelled()
    {
        if (CancelRequested)
            throw new OperationCanceledException("The operation was cancelled.", null, _token);
    }

    /// <summary>Reports progress as a fraction, clamped to [0, 1]. NaN is ignored.</summary>
    public void Report(double fraction)
    {
        if (_progress is null || double.IsNaN(fraction))
            return;
        _progress(Math.Clamp(fraction, 0, 1));
    }
}
