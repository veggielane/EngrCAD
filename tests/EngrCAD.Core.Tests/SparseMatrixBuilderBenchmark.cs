using System.Diagnostics;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Core.Tests;

/// <summary>
/// What <see cref="SparseMatrixBuilder"/>'s packing costs, and what the sort inside it is
/// worth. Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Core.Tests -c Release --filter FullyQualifiedName~SparseMatrixBuilderBenchmark -l "console;verbosity=detailed"
/// </code>
///
/// <para><b>The baseline here is the production code as it stood</b>, transcribed rather
/// than re-derived, and both sides run over the same input in one sitting alternating —
/// this repo has been burned by a 1.88x that turned out to be a <c>Func</c> delegate present
/// only in the baseline, and by ratios taken across sittings on a machine that returns
/// absolute times several-fold apart.</para>
/// </summary>
public class SparseMatrixBuilderBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    /// <summary>
    /// Rows of a symmetric-upper assembly with a stated worst-case raw entry count, built
    /// deterministically: entry counts taper from the longest row to a third of it, and the
    /// columns repeat so duplicates dominate, which is what a finite-element row is.
    /// </summary>
    private static List<(int Col, double Value)>[] Rows(int rowCount, int longestRow, int seed)
    {
        var rng = new Random(seed);
        var rows = new List<(int, double)>[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            int count = longestRow / 3 + rng.Next(longestRow - longestRow / 3 + 1);
            var row = new List<(int, double)>(count);
            // A patch of neighbouring columns, each hit several times — the shape an
            // element loop produces, where every element touching a node contributes the
            // same columns again.
            int distinct = Math.Max(1, count / 7);
            for (int i = 0; i < count; i++)
                row.Add((r + rng.Next(distinct), rng.NextDouble()));
            rows[r] = row;
        }
        return rows;
    }

    /// <summary>
    /// The sort as it stood: a stable insertion sort over the row's own tuples, transcribed
    /// verbatim from the previous implementation, with the identical dedupe walk after it.
    /// </summary>
    private static (int[] Cols, double[] Vals) InsertionSortPack(List<(int Col, double Value)>[] rows)
    {
        var cols = new List<int>();
        var vals = new List<double>();
        var scratch = new List<(int Col, double Value)>();
        foreach (var row in rows)
        {
            scratch.Clear();
            scratch.AddRange(row);
            for (int i = 1; i < scratch.Count; i++)
            {
                var item = scratch[i];
                int j = i - 1;
                while (j >= 0 && scratch[j].Col > item.Col)
                {
                    scratch[j + 1] = scratch[j];
                    j--;
                }
                scratch[j + 1] = item;
            }
            int p = 0;
            while (p < scratch.Count)
            {
                int c = scratch[p].Col;
                double sum = scratch[p].Value;
                int q = p + 1;
                while (q < scratch.Count && scratch[q].Col == c)
                    sum += scratch[q++].Value;
                cols.Add(c);
                vals.Add(sum);
                p = q;
            }
        }
        return ([.. cols], [.. vals]);
    }

    /// <summary>The key sort in isolation, over the same rows, emitting the same thing.</summary>
    private static (int[] Cols, double[] Vals) KeySortPack(List<(int Col, double Value)>[] rows)
    {
        var cols = new List<int>();
        var vals = new List<double>();
        long[] keys = [];
        foreach (var row in rows)
        {
            int count = row.Count;
            if (keys.Length < count)
                keys = new long[Math.Max(count, 2 * keys.Length)];
            var sorted = keys.AsSpan(0, count);
            for (int i = 0; i < count; i++)
                sorted[i] = ((long)row[i].Col << 32) | (uint)i;
            sorted.Sort();
            int p = 0;
            while (p < count)
            {
                int c = (int)(sorted[p] >> 32);
                double sum = row[(int)sorted[p]].Value;
                int q = p + 1;
                while (q < count && (int)(sorted[q] >> 32) == c)
                    sum += row[(int)sorted[q++]].Value;
                cols.Add(c);
                vals.Add(sum);
                p = q;
            }
        }
        return ([.. cols], [.. vals]);
    }

    /// <summary>
    /// The two sorts produce BIT-IDENTICAL packed rows, which is what makes the swap a pure
    /// restructuring: both are stable, so duplicates are summed in add order either way, and
    /// a floating-point sum is a function of its order.
    ///
    /// <para>Runs unconditionally — it is a correctness claim, not a measurement.</para>
    /// </summary>
    [Fact]
    public void TheTwoSortsAgreeBitForBit()
    {
        foreach (int longest in new[] { 3, 24, 90, 612 })
        {
            var rows = Rows(rowCount: 200, longest, seed: 11 + longest);
            var (colsA, valsA) = InsertionSortPack(rows);
            var (colsB, valsB) = KeySortPack(rows);
            Assert.Equal(colsA, colsB);
            Assert.Equal(valsA.Length, valsB.Length);
            for (int i = 0; i < valsA.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(valsA[i]), BitConverter.DoubleToInt64Bits(valsB[i]));
        }
    }

    /// <summary>
    /// The measurement that justified replacing the insertion sort, at the row lengths the
    /// two real consumers produce: a mesh Laplacian's vertex ring (a handful), a 4-node
    /// tetrahedral stiffness row (90 raw entries), and a 10-node one (612).
    /// </summary>
    [Fact]
    public void SortChoiceAcrossRowLengths()
    {
        if (!Enabled)
            return;

        // Warm-up BUDGET, not a warm-up count: JIT tiering makes a single warm-up call
        // meaningless (the same code has measured 147, 314 and 548 Mpts/s across runs).
        var warmRows = Rows(200, 90, 1);
        var warmUntil = Stopwatch.StartNew();
        while (warmUntil.Elapsed.TotalSeconds < 1.5)
        {
            _ = InsertionSortPack(warmRows);
            _ = KeySortPack(warmRows);
        }

        output.WriteLine($"{"longest row",12} {"rows",8} {"entries",10} {"insertion",10} {"key sort",10} {"speedup",8}");
        foreach (var (longest, rowCount) in new[] { (6, 40_000), (24, 20_000), (90, 12_000), (612, 3_000) })
        {
            var rows = Rows(rowCount, longest, seed: 3 + longest);
            int entries = rows.Sum(r => r.Count);
            double insertion = double.MaxValue, key = double.MaxValue;
            for (int trial = 0; trial < 5; trial++)
            {
                var stopwatch = Stopwatch.StartNew();
                _ = InsertionSortPack(rows);
                insertion = Math.Min(insertion, stopwatch.Elapsed.TotalMilliseconds);

                stopwatch.Restart();
                _ = KeySortPack(rows);
                key = Math.Min(key, stopwatch.Elapsed.TotalMilliseconds);
            }
            output.WriteLine(
                $"{longest,12} {rowCount,8:N0} {entries,10:N0} {insertion,10:F1} {key,10:F1} {insertion / key,7:F2}x");
        }
    }
}
