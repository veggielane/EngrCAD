using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>What one tab-meshing job was asked to prepare: the tab's flattened
/// instances (in <c>Tab.Instances()</c> order), the quality to mesh at, and whether the
/// camera should frame the result.</summary>
internal sealed record TabMeshRequest(
    string TabName,
    IReadOnlyList<PartInstance> Instances,
    MeshQuality? Quality,
    bool Frame);

/// <summary>A geometry batch ready to display: the instances meshed so far, in tab
/// order, minus any whose part failed. <see cref="Final"/> marks the last batch of a
/// job (the point at which a host stops showing progress).</summary>
internal readonly record struct TabMeshBatch(
    string TabName,
    IReadOnlyList<PartInstance> Ready,
    IReadOnlyList<Part> Failed,
    bool Frame,
    bool Final);

/// <summary>Progress of the running job, for a determinate bar plus a message naming
/// what is happening. <see cref="Fraction"/> is over the whole tab in [0, 1];
/// <see cref="Flavor"/> is a secondary line describing the route this part takes
/// through the kernel (see <see cref="MeshFlavor"/>).</summary>
internal readonly record struct TabMeshProgress(
    string TabName,
    int Completed,
    int Total,
    string PartName,
    double Fraction,
    string Flavor);

/// <summary>One part that could not be meshed (its geometry never reaches the viewport).</summary>
internal readonly record struct TabMeshFailure(string PartName, string Message);

/// <summary>How a job ended: how many parts it prepared successfully, how long that
/// took, what failed, and whether a newer request (or a cancel) cut it short.</summary>
internal readonly record struct TabMeshCompletion(
    string TabName,
    int PartCount,
    TimeSpan Elapsed,
    IReadOnlyList<TabMeshFailure> Failures,
    bool Cancelled);

/// <summary>
/// Meshes ONE tab's parts on a background task and publishes them as they land — the
/// machinery behind lazy tab meshing, kept free of Avalonia so the state machine is
/// testable headlessly.
/// <para>The rules that make it safe:</para>
/// <list type="bullet">
/// <item><b>Nothing heavy on the UI or render thread.</b> Preparation runs on a worker;
/// every callback is handed back through the <c>post</c> delegate the host supplies
/// (<c>Dispatcher.UIThread.Post</c> in the viewer, a drainable queue in tests).</item>
/// <item><b>A stale result can never land.</b> Each <see cref="Start"/> takes the next
/// generation token; the worker checks it between parts and every callback re-checks it
/// after the post, so a tab switch mid-job discards both the work and its results —
/// results are never applied to the wrong tab.</item>
/// <item><b>Work already done is never wasted.</b> Parts cache their mesh, so a job
/// starts from the longest already-meshed prefix (a revisit publishes the whole tab
/// synchronously, with no background work at all) and a cancelled part that had already
/// finished stays cached for the next visit. A part cancelled MID-flight keeps the step
/// that cannot be abandoned safely — its B-Rep lowering — so the next visit resumes from
/// there.</item>
/// <item><b>A part that throws is reported, not swallowed.</b> It is dropped from the
/// published instances (its geometry cannot be uploaded) and named in the completion,
/// while the rest of the tab still loads and the bar still reaches the end.</item>
/// </list>
/// </summary>
internal sealed class TabMeshLoader
{
    private readonly Action<Action> _post;
    private readonly Action<Part, MeshQuality?, ProgressCancel?> _prepare;
    private readonly Lock _gate = new();
    private int _generation;
    private CancellationTokenSource? _cancel;

    /// <param name="post">Hands a callback to the host's UI thread.</param>
    /// <param name="prepare">Prepares one part (default <see cref="Part.Prepare"/>);
    /// tests substitute a fake to drive failures and timing.</param>
    public TabMeshLoader(Action<Action> post, Action<Part, MeshQuality?, ProgressCancel?>? prepare = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _prepare = prepare ?? ((part, quality, progress) => part.Prepare(quality, progress));
    }

    /// <summary>Geometry ready to display (UI thread). Always fires at least once per
    /// request, so the host can swap tabs even when nothing needs meshing.</summary>
    public Action<TabMeshBatch>? Ready { get; set; }

    /// <summary>Progress of a job that has background work to do (UI thread). Never
    /// fires for an already-meshed tab.</summary>
    public Action<TabMeshProgress>? Progress { get; set; }

    /// <summary>Fires once when a job that did background work ends (UI thread),
    /// including when it was cancelled or had failures.</summary>
    public Action<TabMeshCompletion>? Completed { get; set; }

    /// <summary>The token of the job that currently owns the viewport. Every callback
    /// carries it implicitly: results from an older generation are dropped.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Starts (or, for an already-meshed tab, immediately completes) a job for
    /// <paramref name="request"/>, superseding any job still running. Returns the worker
    /// task — <see cref="Task.CompletedTask"/> when everything was cached, which is the
    /// "revisiting a tab is instant" path.
    /// </summary>
    public Task Start(TabMeshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int token;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            _cancel?.Cancel();   // the previous job stops at its next part boundary
            _cancel = cancel = new CancellationTokenSource();
            token = ++_generation;
        }

        // Start from what is already meshed: a revisited tab is entirely cached and
        // publishes in one go, and a tab sharing parts with the last one starts part-way.
        var instances = request.Instances;
        int ready = 0;
        while (ready < instances.Count && instances[ready].Part.HasMesh)
            ready++;

        bool nothingToDo = ready == instances.Count;
        Publish(request, token, instances.Take(ready).ToList(), [], final: nothingToDo);
        return nothingToDo ? Task.CompletedTask : Task.Run(() => Work(request, token, cancel, ready));
    }

    /// <summary>Abandons the running job; nothing more is published. The part in flight
    /// stops where it is and keeps whatever cannot be abandoned safely — a B-Rep part's
    /// lowering, which is the expensive half — so a revisit resumes from there rather than
    /// from nothing.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancel?.Cancel();
            _cancel = null;
            _generation++;
        }
    }

    private void Work(TabMeshRequest request, int token, CancellationTokenSource cancel, int start)
    {
        var clock = Stopwatch.StartNew();
        var instances = request.Instances;
        var failures = new List<TabMeshFailure>();
        var failed = new List<Part>();

        // Distinct parts: an assembly places one part many times, and it meshes once.
        var prepared = new HashSet<Part>();
        for (int i = 0; i < start; i++)
            prepared.Add(instances[i].Part);
        int total = instances.Select(i => i.Part).Distinct().Count() - prepared.Count;
        int done = 0;
        bool cancelled = false;

        for (int i = start; i < instances.Count; i++)
        {
            if (cancel.IsCancellationRequested || IsStale(token))
            {
                cancelled = true;
                break;
            }

            var part = instances[i].Part;
            if (!prepared.Add(part))
                continue;   // another instance of a part this job already meshed

            int completed = done;
            string flavor = MeshFlavor.For(part);
            Report(request, token, completed, total, part.Name, flavor, 0);
            double last = 0;
            var progress = new ProgressCancel(
                () => cancel.IsCancellationRequested || IsStale(token),
                fraction =>
                {
                    // Sub-part fractions can arrive in a flood; one UI update per
                    // percent is plenty and keeps the dispatcher free.
                    if (fraction - last < 0.01 && fraction < 1)
                        return;
                    last = fraction;
                    Report(request, token, completed, total, part.Name, flavor, fraction);
                });

            try
            {
                _prepare(part, request.Quality, progress);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception e)
            {
                // The part is out (its geometry cannot be uploaded), the tab is not:
                // keep meshing, keep publishing, and name it in the completion.
                failed.Add(part);
                failures.Add(new TabMeshFailure(part.Name, Describe(e)));
            }

            done++;
            Report(request, token, done, total, part.Name, flavor, 1);
            Publish(request, token, ReadyInstances(instances, prepared, failed), failed, final: false);
        }

        if (!cancelled)
            Publish(request, token, ReadyInstances(instances, prepared, failed), failed, final: true);

        var completion = new TabMeshCompletion(
            request.TabName, done - failures.Count, clock.Elapsed, failures, cancelled);
        Post(token, () => Completed?.Invoke(completion));
    }

    /// <summary>The instances a batch may show: those whose part this job has prepared
    /// (or found already meshed), minus the ones that failed — order preserved, so the
    /// host's row indices line up with the viewport's.</summary>
    private static List<PartInstance> ReadyInstances(
        IReadOnlyList<PartInstance> instances, HashSet<Part> prepared, List<Part> failed)
    {
        var ready = new List<PartInstance>(instances.Count);
        foreach (var instance in instances)
        {
            if (prepared.Contains(instance.Part) && !failed.Contains(instance.Part))
                ready.Add(instance);
        }
        return ready;
    }

    private void Publish(
        TabMeshRequest request, int token, IReadOnlyList<PartInstance> ready,
        IReadOnlyList<Part> failed, bool final)
    {
        var batch = new TabMeshBatch(request.TabName, ready, [.. failed], request.Frame, final);
        Post(token, () => Ready?.Invoke(batch));
    }

    private void Report(
        TabMeshRequest request, int token, int completed, int total,
        string partName, string flavor, double partFraction)
    {
        double fraction = total <= 0 ? 1 : Math.Clamp((completed + partFraction) / total, 0, 1);
        var progress = new TabMeshProgress(
            request.TabName, completed, total, partName, fraction, flavor);
        Post(token, () => Progress?.Invoke(progress));
    }

    /// <summary>Hands a callback to the host's thread, dropped if a newer request has
    /// taken over by the time it runs — the second half of the staleness guard (the
    /// first is the worker's own between-parts check).</summary>
    private void Post(int token, Action callback) => _post(() =>
    {
        if (!IsStale(token))
            callback();
    });

    private bool IsStale(int token) => Volatile.Read(ref _generation) != token;

    private static string Describe(Exception e)
    {
        while (e is System.Reflection.TargetInvocationException { InnerException: { } inner })
            e = inner;
        return $"{e.GetType().Name}: {e.Message}";
    }
}
