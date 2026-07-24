namespace EngrCAD.Core;

/// <summary>
/// Thin block-parallel-for over index ranges, suited to the kernel's struct-of-arrays
/// loops (grid sampling, SDF bakes): the range is split into a bounded number of large
/// contiguous blocks and each block runs as one work item, so per-item delegate overhead
/// stays negligible and every worker touches a contiguous slice of the underlying
/// arrays. Modeled on geometry3Sharp's <c>gParallel.BlockStartEnd</c>, built on
/// <see cref="System.Threading.Tasks.Parallel"/>.
/// </summary>
/// <remarks>
/// Determinism contract: which indices land in which block depends only on the range
/// and the machine's processor count — callers that write each index's result to its
/// own output slot (the only supported pattern) therefore produce bit-for-bit identical
/// results regardless of scheduling. The body must be thread-safe and must not write
/// outside its own index range.
/// </remarks>
public static class ParallelFor
{
    /// <summary>
    /// Runs <paramref name="body"/>(startInclusive, endExclusive) over contiguous blocks
    /// partitioning [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>),
    /// possibly concurrently. Ranges smaller than <paramref name="minBlockSize"/> run
    /// inline on the calling thread.
    /// </summary>
    /// <param name="fromInclusive">Start of the index range.</param>
    /// <param name="toExclusive">End of the index range (exclusive).</param>
    /// <param name="body">Block worker; receives a [start, end) sub-range.</param>
    /// <param name="minBlockSize">
    /// Minimum indices per block (default 1). Raise it when a single index is cheap, so
    /// blocks stay coarse enough to amortize scheduling.
    /// </param>
    public static void Blocks(int fromInclusive, int toExclusive, Action<int, int> body, int minBlockSize = 1)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (minBlockSize < 1)
            throw new ArgumentOutOfRangeException(nameof(minBlockSize));
        int count = toExclusive - fromInclusive;
        if (count <= 0)
            return;

        // A few blocks per worker balances load without shrinking blocks too far.
        int maxBlocks = Environment.ProcessorCount * 4;
        int blocks = Math.Min(maxBlocks, (count + minBlockSize - 1) / minBlockSize);
        if (blocks <= 1)
        {
            body(fromInclusive, toExclusive);
            return;
        }

        int blockSize = (count + blocks - 1) / blocks;
        System.Threading.Tasks.Parallel.For(0, blocks, block =>
        {
            int start = fromInclusive + block * blockSize;
            int end = Math.Min(start + blockSize, toExclusive);
            if (start < end)
                body(start, end);
        });
    }
}
