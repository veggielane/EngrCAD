using EngrCAD.TestSupport;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The probe is a guard, so it is held to a guard's bar: it must be shown to FIRE on a
/// real per-iteration allocation, and to be immune to the one-time artifact it exists to
/// absorb. Asserting only that a clean batch reports zero would pass just as happily for
/// a probe that always returned zero.
/// </summary>
public class AllocationProbeTests
{
    [Fact]
    public void ReportsZeroForAnAllocationFreeBatch()
    {
        double sink = 0;
        long bytes = AllocationProbe.SteadyStateBytes(() =>
        {
            for (int i = 0; i < 1000; i++)
                sink += i * 0.5;
        });

        Assert.Equal(0, bytes);
        Assert.True(double.IsFinite(sink)); // keep the loop observable
    }

    /// <summary>The mutation check: a per-iteration allocation must be caught, and caught
    /// at its true size, since the minimum over batches is still a full batch's worth.</summary>
    [Fact]
    public void CatchesAPerIterationAllocation()
    {
        const int iterations = 1000;
        object? sink = null;
        long bytes = AllocationProbe.SteadyStateBytes(() =>
        {
            for (int i = 0; i < iterations; i++)
                sink = new object();
        });

        // An object header + method table pointer is 24 B on x64; assert the SCALE rather
        // than the exact figure, which is a runtime detail.
        Assert.True(bytes >= iterations * 16,
            $"expected ≥ {iterations * 16} B for {iterations} allocations, measured {bytes}");
        Assert.NotNull(sink);
    }

    /// <summary>
    /// The property the minimum exists for, pinned directly rather than left to machine
    /// load to reproduce: a ONE-TIME artifact — a tiering promotion, or a neighbouring
    /// xUnit thread's GC refreshing this thread's allocation context — spoils a bounded
    /// number of batches, and must not be reported as a steady-state cost.
    /// </summary>
    [Fact]
    public void IgnoresAOneTimeArtifact()
    {
        int batch = 0;
        object? sink = null;
        long bytes = AllocationProbe.SteadyStateBytes(() =>
        {
            // Allocates generously on the warm-up and the first TWO measured batches, then
            // never again — the shape of a late tier promotion.
            if (batch++ < 3)
                sink = new byte[64 * 1024];
        });

        Assert.Equal(0, bytes);
        Assert.NotNull(sink);
        Assert.Equal(AllocationProbe.DefaultBatches + 1, batch); // warm-up + measured
    }

    /// <summary>A single measured batch is the incumbent shape, and it is exactly what the
    /// artifact above defeats — so one batch must NOT be offered as a silent default.</summary>
    [Fact]
    public void ASingleBatchIsStillHonest_ButCarriesTheArtifact()
    {
        int batch = 0;
        object? sink = null;
        long bytes = AllocationProbe.SteadyStateBytes(
            () =>
            {
                if (batch++ < 2) // warm-up plus the one measured batch
                    sink = new byte[64 * 1024];
            },
            batches: 1);

        Assert.True(bytes >= 64 * 1024, "one batch cannot distinguish a one-time artifact");
        Assert.NotNull(sink);
    }

    [Fact]
    public void RejectsNonsenseArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AllocationProbe.SteadyStateBytes(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AllocationProbe.SteadyStateBytes(() => { }, batches: 0));
    }
}
