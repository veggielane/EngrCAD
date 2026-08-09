using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>Where one run sits in a linked order, and which way round it is drawn.</summary>
/// <param name="Index">The run's index in the original list.</param>
/// <param name="Reversed">True when the run is drawn from its END to its start — free, since a
/// clipped stretch of a space-filling curve is as valid drawn either way, and half of what a
/// linker has to search.</param>
public readonly record struct LinkedRun(int Index, bool Reversed);

/// <summary>
/// The travel between runs: which order to draw them in and which way round, plus what that
/// cost. ONE linker, deliberately — a clipped infill, a pocket-clearing pass and a 2.5D
/// contour set all ask the same question, and two of them would drift.
///
/// <para><b>Greedy nearest-endpoint, and deterministic.</b> From the current pen position, take
/// the unvisited run whose nearer END is closest, and draw it from that end. Ties break on the
/// lower run index and then on not-reversed, so the answer is a function of the input and of
/// nothing else — the property a toolpath needs in order to be reproducible, and the reason no
/// randomised improvement (2-opt with restarts, simulated annealing) is offered here.</para>
///
/// <para><b>It is a heuristic and says so.</b> Ordering runs to minimise travel is the open
/// travelling-salesman problem in disguise, so what is promised is a measured IMPROVEMENT over
/// the incumbent order rather than an optimum: <see cref="PathLinkage.TravelLength"/> beside
/// <see cref="PathLinkage.SourceOrderTravelLength"/>, which is what a caller compares. Greedy is
/// the right heuristic for THIS input for a structural reason — a space-filling curve's clipped
/// runs are already in an order where consecutive runs are spatial neighbours, so the incumbent
/// order is a good tour and the linker's job is picking up the ends it left behind.</para>
/// </summary>
public static class RunLinker
{
    /// <summary>
    /// Orders <paramref name="ends"/> — one (start, end) pair per run, in source order — into a
    /// linked tour, beginning at <paramref name="from"/> (null starts at the first run's start,
    /// so run 0 is drawn first and forwards).
    /// </summary>
    public static PathLinkage Link(IReadOnlyList<(Vector3d Start, Vector3d End)> ends, Vector3d? from = null)
    {
        ArgumentNullException.ThrowIfNull(ends);
        if (ends.Count == 0)
            return new PathLinkage([], 0, 0);

        double sourceTravel = 0;
        for (int i = 1; i < ends.Count; i++)
            sourceTravel += ends[i].Start.DistanceTo(ends[i - 1].End);

        var order = new List<LinkedRun>(ends.Count);
        var taken = new bool[ends.Count];
        var pen = from ?? ends[0].Start;
        double travel = 0;

        for (int placed = 0; placed < ends.Count; placed++)
        {
            int best = -1;
            bool bestReversed = false;
            double bestCost = double.PositiveInfinity;
            for (int i = 0; i < ends.Count; i++)
            {
                if (taken[i])
                    continue;
                double forward = pen.DistanceTo(ends[i].Start);
                double backward = pen.DistanceTo(ends[i].End);
                // Strict comparisons throughout, so the first index wins a tie and a forward
                // draw wins a tie against its own reversal: the order is a function of the
                // input, never of iteration luck.
                if (forward < bestCost)
                {
                    bestCost = forward;
                    best = i;
                    bestReversed = false;
                }
                if (backward < bestCost)
                {
                    bestCost = backward;
                    best = i;
                    bestReversed = true;
                }
            }

            taken[best] = true;
            order.Add(new LinkedRun(best, bestReversed));
            travel += bestCost;
            pen = bestReversed ? ends[best].Start : ends[best].End;
        }

        // The move onto the FIRST run is travel too when the caller stated a pen position; with
        // no stated position the tour starts on the path and that leg is zero by construction.
        return new PathLinkage(order, travel, sourceTravel);
    }

    /// <summary>The (start, end) pairs of a list of 2D runs, lifted into the z = 0 plane the
    /// linker works in — one arithmetic, two dimensions.</summary>
    public static IReadOnlyList<(Vector3d Start, Vector3d End)> EndsOf(
        IReadOnlyList<IReadOnlyList<Vector2d>> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var ends = new (Vector3d, Vector3d)[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            ends[i] = (new Vector3d(run[0].X, run[0].Y, 0), new Vector3d(run[^1].X, run[^1].Y, 0));
        }
        return ends;
    }

    /// <summary>The (start, end) pairs of a list of 3D runs.</summary>
    public static IReadOnlyList<(Vector3d Start, Vector3d End)> EndsOf(
        IReadOnlyList<IReadOnlyList<Vector3d>> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var ends = new (Vector3d, Vector3d)[runs.Count];
        for (int i = 0; i < runs.Count; i++)
            ends[i] = (runs[i][0], runs[i][^1]);
        return ends;
    }
}

/// <summary>One linked tour over a set of runs — see <see cref="RunLinker"/>.</summary>
public sealed class PathLinkage
{
    internal PathLinkage(IReadOnlyList<LinkedRun> order, double travelLength, double sourceOrderTravelLength)
    {
        Order = order;
        TravelLength = travelLength;
        SourceOrderTravelLength = sourceOrderTravelLength;
    }

    /// <summary>The runs in draw order, each saying which way round it is drawn. A permutation
    /// of the input by construction — every run appears exactly once, so a linker can shorten
    /// the travel and can never quietly drop a pass.</summary>
    public IReadOnlyList<LinkedRun> Order { get; }

    /// <summary>The total travel this order costs.</summary>
    public double TravelLength { get; }

    /// <summary>What the runs' own order costs, drawn forwards — the baseline the improvement is
    /// measured against, reported rather than implied so a caller can see when the incumbent
    /// order was already good.</summary>
    public double SourceOrderTravelLength { get; }

    /// <summary>How many times the tool lifts: one fewer than the run count.</summary>
    public int TravelMoves => Math.Max(0, Order.Count - 1);

    /// <summary>The factor by which linking shortened the travel. 1 when it changed nothing;
    /// infinite in the degenerate case where the linked travel is exactly zero.</summary>
    public double Improvement => TravelLength > 0 ? SourceOrderTravelLength / TravelLength : double.PositiveInfinity;

    /// <summary>Applies this order to the runs it was computed from, reversing where
    /// <see cref="LinkedRun.Reversed"/> says so. Generic in the point type, which is what lets
    /// the 2D and the 3D consumer share one linker rather than one each.</summary>
    public IReadOnlyList<IReadOnlyList<T>> Reorder<T>(IReadOnlyList<IReadOnlyList<T>> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count != Order.Count)
        {
            throw new ArgumentException(
                $"This linkage orders {Order.Count} runs; {runs.Count} were given.", nameof(runs));
        }
        var result = new IReadOnlyList<T>[Order.Count];
        for (int i = 0; i < Order.Count; i++)
        {
            var run = runs[Order[i].Index];
            result[i] = Order[i].Reversed ? [.. run.Reverse()] : run;
        }
        return result;
    }
}
