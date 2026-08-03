using EngrCAD.Core;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Modeling;

/// <summary>
/// A Hamiltonian path over a RECTANGULAR lattice, tiled from square Hilbert blocks — the
/// "tiled Hilbert blocks with their ends linked" the infill residuals name, and what makes a
/// tamper mesh cover a wall rather than a square patch of one.
///
/// <para><b>Why it needs no new curve.</b> An order-n Hilbert block starts at cell
/// <c>(0, 0)</c> and ends at <c>(m − 1, 0)</c> — two ADJACENT CORNERS of its square, both on
/// its bottom edge. Under the eight symmetries of the square that ordered pair becomes any of
/// the eight (entry, exit) pairs of adjacent corners, and a lattice isometry maps a
/// Hamiltonian path to a Hamiltonian path. So the whole construction is a boustrophedon over
/// the BLOCK grid in which each block is asked for the entry and exit corner its neighbours
/// need, and the answer is the symmetry that provides them.</para>
///
/// <para><b>The chaining rule, which is the only thing to get right.</b> A block leaves along
/// one edge of its square, so the next block must be reached from a cell on that edge.
/// Rightward rows run <c>BL → BR</c> (each block's exit is horizontally adjacent to the next
/// one's entry); the last block of a row instead runs <c>BL → TL</c> so the exit is in the top
/// row, directly under the next row's entry; leftward rows run <c>TR → TL</c>, with their
/// first block <c>BL → TL</c>. Continuity is not assumed anywhere — the caller asserts it with
/// <c>SpaceFillingCurve.AreNeighbours</c>, which is exact integer arithmetic.</para>
///
/// <para><b>It reduces exactly.</b> One block (<c>1 × 1</c>) is Core's own Hilbert lattice
/// site for site; block order 0 makes every block a single cell and the route a plain
/// boustrophedon serpentine, which is a legitimate member rather than a degenerate one — see
/// the trade recorded on <see cref="TamperMesh.DefaultBlockOrder"/>.</para>
/// </summary>
internal static class TiledHilbertRoute
{
    /// <summary>The four corners of a block, as the (entry, exit) vocabulary the chaining
    /// rule is written in.</summary>
    private enum Corner
    {
        BottomLeft,
        BottomRight,
        TopRight,
        TopLeft,
    }

    /// <summary>
    /// The route over a <c>blocksX·2^order</c> by <c>blocksY·2^order</c> lattice, in visit
    /// order.
    /// </summary>
    public static Vector2i[] Build(int order, int blocksX, int blocksY)
    {
        if (order < 0)
            throw new ArgumentOutOfRangeException(nameof(order));
        if (blocksX < 1 || blocksY < 1)
            throw new ArgumentOutOfRangeException(nameof(blocksX), "A tiled route needs at least one block each way.");

        int m = 1 << order;
        var block = SpaceFillingCurve.LatticeSites(SpaceFillingFamily.Hilbert, order);

        // The whole construction rests on where the base block's ends are, so it is checked
        // rather than trusted: a change in Core would otherwise surface as a broken net.
        if (block[0].X != 0 || block[0].Y != 0 || block[^1].X != m - 1 || block[^1].Y != 0)
        {
            throw new InvalidOperationException(
                $"The order-{order} Hilbert block runs {block[0]} to {block[^1]}; the tiling needs "
                + $"(0, 0) to ({m - 1}, 0), the two ends of its bottom edge.");
        }

        long total = (long)block.Count * blocksX * blocksY;
        if (total > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(blocksX), "The tiled route does not fit in one array.");
        var route = new Vector2i[(int)total];

        int at = 0;
        for (int row = 0; row < blocksY; row++)
        {
            bool rightward = row % 2 == 0;
            bool lastRow = row == blocksY - 1;
            for (int k = 0; k < blocksX; k++)
            {
                int column = rightward ? k : blocksX - 1 - k;
                bool lastInRow = k == blocksX - 1;
                var (entry, exit) = Chaining(rightward, k == 0, lastInRow, lastRow);
                int transform = TransformFor(entry, exit);
                foreach (var cell in block)
                {
                    var mapped = Apply(transform, cell, m);
                    route[at++] = new Vector2i(column * m + mapped.X, row * m + mapped.Y);
                }
            }
        }
        return route;
    }

    /// <summary>Which corners a block must be entered and left by, given where it sits in the
    /// boustrophedon. See the class remarks — every case is forced by its neighbours except
    /// the very last block's exit, which is free and is kept on the row's own side so the two
    /// terminals stay on the footprint boundary.</summary>
    private static (Corner Entry, Corner Exit) Chaining(
        bool rightward, bool firstInRow, bool lastInRow, bool lastRow)
    {
        if (rightward)
        {
            // Entered from the left neighbour's bottom-right, or from the row below's top-left;
            // either way at this block's bottom-left.
            return lastInRow && !lastRow
                ? (Corner.BottomLeft, Corner.TopLeft)      // turn upward into the next row
                : (Corner.BottomLeft, Corner.BottomRight); // carry on right (or stop)
        }
        // A leftward row leaves along its LEFT edge, so every block exits top-left; the first
        // one is entered from below (bottom-left) and the rest from their right neighbour's
        // top-left, which is their own top-right.
        return firstInRow
            ? (Corner.BottomLeft, Corner.TopLeft)
            : (Corner.TopRight, Corner.TopLeft);
    }

    /// <summary>The eight symmetries of the square, indexed so
    /// <see cref="TransformFor"/> can pick one by the corner pair it produces.</summary>
    private static Vector2i Apply(int transform, in Vector2i cell, int m)
    {
        int x = cell.X, y = cell.Y, last = m - 1;
        return transform switch
        {
            0 => new Vector2i(x, y),                    // identity
            1 => new Vector2i(last - y, x),             // quarter turn, counter-clockwise
            2 => new Vector2i(last - x, last - y),      // half turn
            3 => new Vector2i(y, last - x),             // quarter turn, clockwise
            4 => new Vector2i(last - x, y),             // mirror in x
            5 => new Vector2i(y, x),                    // transpose
            6 => new Vector2i(x, last - y),             // mirror in y
            7 => new Vector2i(last - y, last - x),      // anti-transpose
            _ => throw new ArgumentOutOfRangeException(nameof(transform)),
        };
    }

    /// <summary>Which symmetry takes the base block's (bottom-left, bottom-right) ends to the
    /// stated pair. Derived by applying each transform to the two end corners rather than
    /// tabulated by hand — the table and the transforms then cannot disagree.</summary>
    private static int TransformFor(Corner entry, Corner exit)
    {
        const int nominal = 4;   // any m >= 2: corner classification does not depend on it
        for (int t = 0; t < 8; t++)
        {
            var start = Apply(t, new Vector2i(0, 0), nominal);
            var end = Apply(t, new Vector2i(nominal - 1, 0), nominal);
            if (Classify(start, nominal) == entry && Classify(end, nominal) == exit)
                return t;
        }
        throw new ArgumentOutOfRangeException(
            nameof(entry), $"No symmetry of the square takes a block from {entry} to {exit}; "
            + "the two must be adjacent corners.");
    }

    private static Corner Classify(in Vector2i corner, int m) =>
        (corner.X == 0, corner.Y == 0) switch
        {
            (true, true) => Corner.BottomLeft,
            (false, true) => Corner.BottomRight,
            (false, false) => Corner.TopRight,
            (true, false) => Corner.TopLeft,
        };
}
