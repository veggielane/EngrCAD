using Xunit;

namespace EngrCAD.Core.Tests;

public class ParallelForTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0, 100, 1)]
    [InlineData(5, 1234, 7)]
    [InlineData(-10, 10, 64)]
    [InlineData(0, 100_000, 1)]
    public void Blocks_CoverTheRangeExactlyOnce(int from, int to, int minBlockSize)
    {
        int count = to - from;
        var visits = new int[count];
        ParallelFor.Blocks(from, to, (start, end) =>
        {
            Assert.True(start < end);
            for (int i = start; i < end; i++)
                Interlocked.Increment(ref visits[i - from]);
        }, minBlockSize);

        Assert.All(visits, v => Assert.Equal(1, v));
    }

    [Fact]
    public void EmptyOrInvertedRange_InvokesNothing()
    {
        int calls = 0;
        ParallelFor.Blocks(5, 5, (_, _) => Interlocked.Increment(ref calls));
        ParallelFor.Blocks(5, 3, (_, _) => Interlocked.Increment(ref calls));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SmallRange_RunsInlineAsOneBlock()
    {
        // count <= minBlockSize collapses to a single inline invocation.
        var ranges = new List<(int, int)>();
        ParallelFor.Blocks(2, 9, (start, end) => { lock (ranges) ranges.Add((start, end)); }, minBlockSize: 100);
        Assert.Equal([(2, 9)], ranges);
    }

    [Fact]
    public void PerSlotWrites_AreDeterministic()
    {
        // The supported pattern: each index computes its own slot. Two runs must agree
        // bit-for-bit whatever the scheduling.
        double[] Run()
        {
            var output = new double[10_000];
            ParallelFor.Blocks(0, output.Length, (start, end) =>
            {
                for (int i = start; i < end; i++)
                    output[i] = Math.Sin(i * 0.001) * Math.Sqrt(i + 1);
            });
            return output;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Exceptions_Propagate()
    {
        Assert.ThrowsAny<Exception>(() =>
            ParallelFor.Blocks(0, 1000, (_, _) => throw new InvalidOperationException("boom")));
    }
}
