namespace EngrCAD.TestSupport;

/// <summary>
/// Measures the steady-state allocation of a repeated operation, immune to the one-time
/// artifacts that make a single measured batch flaky.
/// </summary>
/// <remarks>
/// <para>
/// The obvious shape — warm up N times, measure one batch, assert the total is small —
/// is wrong for a reason this repo has already recorded once about benchmarks: <b>a
/// warm-up COUNT does not establish steady state, because tiered compilation is promoted
/// on a background queue and therefore on a WALL CLOCK</b>. Under machine load the
/// promotion (and its on-stack-replacement transition) slips past the warm-up and lands
/// inside the measured window. <see cref="GC.GetAllocatedBytesForCurrentThread"/> is also
/// perturbed by a garbage collection triggered on ANOTHER thread — xUnit runs collections
/// in parallel — since a collection refreshes this thread's allocation context.
/// </para>
/// <para>
/// Both artifacts are ONE-TIME and neither scales with the iteration count, so the fix is
/// to take the <b>minimum</b> over several equal batches rather than to loosen the
/// threshold. The argument is exact: a genuine per-iteration allocation is present in
/// every batch, so the minimum is still at least <c>iterations × cost</c> and the test
/// cannot be escaped by retrying; a one-time artifact can only spoil a bounded number of
/// batches, so with several of them at least one is clean. That is precisely what
/// "steady state" means, which is what these tests are named for — where a bigger
/// tolerance would instead buy the flake off by making the test blind to a small real
/// regression.
/// </para>
/// <para>
/// Deliberately NOT a wall-clock warm-up budget: it would make the tests slower and it
/// would still only make the artifact unlikely, where the minimum makes it irrelevant.
/// </para>
/// </remarks>
internal static class AllocationProbe
{
    /// <summary>The number of measured batches. Five makes a false failure need five
    /// independent one-time artifacts in a row.</summary>
    public const int DefaultBatches = 5;

    /// <summary>
    /// Runs <paramref name="runBatch"/> once to warm up, then <paramref name="batches"/>
    /// times under measurement, and returns the SMALLEST allocation any batch made.
    /// </summary>
    /// <param name="runBatch">Performs one batch. Must do the same work every call —
    /// a batch that grows a cache would report the growth as a per-iteration cost.</param>
    /// <param name="batches">How many batches to measure.</param>
    public static long SteadyStateBytes(Action runBatch, int batches = DefaultBatches)
    {
        ArgumentNullException.ThrowIfNull(runBatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(batches, 1);

        runBatch(); // warm-up: tiering, pool fills, collection capacity growth

        long best = long.MaxValue;
        for (int i = 0; i < batches; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            runBatch();
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return best;
    }
}
