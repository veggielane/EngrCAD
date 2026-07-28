namespace EngrCAD.Core.Solvers;

/// <summary>
/// Approximate minimum degree (AMD) fill-reducing ordering for a symmetric sparse
/// pattern — Amestoy, Davis and Duff, <i>An approximate minimum degree ordering
/// algorithm</i>, SIAM J. Matrix Anal. Appl. 17(4), 1996.
/// </summary>
/// <remarks>
/// <para>
/// Cholesky's fill is decided entirely by the elimination order, and the classic
/// heuristic — eliminate the node of least degree next — is expensive because after each
/// pivot the true degrees of the whole neighbourhood have to be recomputed. AMD replaces
/// the graph by a <b>quotient graph</b>: eliminated pivots become <em>elements</em> whose
/// adjacency list is the clique they created, so the pattern never grows past the
/// original nonzero count, and it replaces the true degree by an upper bound that costs
/// one pass over the pivot's element list. The bound is
/// <c>d(i) ≤ min(n − k, d(i)+|Lk\i|, |Ai\i| + |Lk\i| + Σ|Le\Lk|)</c>, tight in practice
/// and never worse than the true degree by more than the overlap it cannot see.
/// </para>
/// <para>
/// Three refinements do most of the practical work and are all implemented here:
/// <b>element absorption</b> (an element wholly inside the new one is deleted, including
/// the aggressive form that fires as soon as its set difference goes empty),
/// <b>mass elimination</b> (a node whose remaining adjacency is exactly the new element
/// is eliminated with it, at no extra fill), and <b>supervariables</b> — nodes with
/// identical adjacency are detected by hashing and merged, which is what makes the
/// method near-linear on the grid-like patterns this repo produces.
/// </para>
/// <para>
/// The result is a <b>postorder of the assembly tree</b> rather than the raw pivot
/// sequence: the two give the same fill, and the postorder makes the factor's columns
/// contiguous per supernode, which is friendlier to the substitution loops.
/// </para>
/// <para>
/// Deterministic: no randomness, no tie-break that depends on scheduling. The same
/// pattern always yields the same permutation.
/// </para>
/// </remarks>
internal static class AmdOrdering
{
    /// <summary>
    /// A reversible tag for "this slot now holds a tree parent, not a list pointer".
    /// Its own inverse, and it maps every valid index to a value below −1, so the −1
    /// root marker and the ≥ 0 live pointers stay distinguishable.
    /// </summary>
    private static int Flip(int i) => -i - 2;

    /// <summary>
    /// Orders the symmetric pattern whose upper triangle is given in compressed-column
    /// form. Returns <c>perm</c> of length <paramref name="n"/> with <c>perm[k]</c> = the
    /// original index that becomes index <c>k</c>.
    /// </summary>
    public static int[] Order(int n, ReadOnlySpan<int> upperColStart, ReadOnlySpan<int> upperRowIndex)
    {
        if (n <= 0)
            return [];
        if (n == 1)
            return [0];

        // ---- the symmetric pattern A + Aᵀ without its diagonal ----------------------
        // AMD works on the undirected graph of A; the diagonal is a self-loop and would
        // only inflate every degree by one.
        var len = new int[n + 1];
        for (int k = 0; k < n; k++)
        {
            for (int p = upperColStart[k]; p < upperColStart[k + 1]; p++)
            {
                int i = upperRowIndex[p];
                if (i == k)
                    continue;
                len[i]++;
                len[k]++;
            }
        }

        var start = new int[n + 1];
        int nz = 0;
        for (int i = 0; i < n; i++)
        {
            start[i] = nz;
            nz += len[i];
        }
        start[n] = nz;

        // Elbow room: eliminating a pivot appends its element list at the end of the
        // arena, so the arena has to hold more than the original pattern. When it fills,
        // Compact() slides the live lists down; 20% plus 2n keeps that rare.
        var adjacency = new int[nz + nz / 5 + 2 * n + 8];
        var cursor = new int[n];
        Array.Copy(start, cursor, n);
        for (int k = 0; k < n; k++)
        {
            for (int p = upperColStart[k]; p < upperColStart[k + 1]; p++)
            {
                int i = upperRowIndex[p];
                if (i == k)
                    continue;
                adjacency[cursor[i]++] = k;
                adjacency[cursor[k]++] = i;
            }
        }

        // ---- quotient-graph state ---------------------------------------------------
        // Every array is sized n + 1: index n is a virtual element that swallows the
        // "dense" nodes (those whose degree is so large that ordering them by degree
        // costs more than it saves). They are eliminated last, together.
        var nodeSize = new int[n + 1];      // supervariable size; < 0 flags "in the current element"
        var nextInList = new int[n + 1];    // degree-list / hash-bucket successor
        var listHead = new int[n + 1];      // head of the degree list of that degree
        var elementCount = new int[n + 1];  // |Ei|; -1 = dead node, -2 = element
        var degree = new int[n + 1];        // approximate external degree
        var work = new int[n + 1];          // set-difference marks; 0 = dead element
        var hashHead = new int[n + 1];
        var last = new int[n + 1];          // degree-list predecessor, later the node's hash

        for (int i = 0; i <= n; i++)
        {
            listHead[i] = -1;
            last[i] = -1;
            nextInList[i] = -1;
            hashHead[i] = -1;
            nodeSize[i] = 1;
            work[i] = 1;
            elementCount[i] = 0;
            degree[i] = len[i];
        }
        len[n] = 0;
        elementCount[n] = -2;   // n is an element from the outset, and a dead one
        start[n] = -1;          // ... and a root of the assembly tree
        work[n] = 0;

        int mark = ClearMarks(0, 0, work, n);

        // The dense-node cutoff of the published algorithm. Clamped to n - 2 so a tiny
        // matrix cannot declare every node dense and lose the ordering entirely.
        int denseCutoff = Math.Min(n - 2, Math.Max(16, (int)(10.0 * Math.Sqrt(n))));

        int eliminated = 0;
        for (int i = 0; i < n; i++)
        {
            int d = degree[i];
            if (d == 0)
            {
                // An isolated node: eliminating it creates no fill, so retire it now and
                // make it its own tree root.
                elementCount[i] = -2;
                eliminated++;
                start[i] = -1;
                work[i] = 0;
            }
            else if (d > denseCutoff)
            {
                nodeSize[i] = 0;            // absorbed into the virtual element n
                elementCount[i] = -1;
                eliminated++;
                start[i] = Flip(n);
                nodeSize[n]++;
            }
            else
            {
                if (listHead[d] != -1)
                    last[listHead[d]] = i;
                nextInList[i] = listHead[d];
                listHead[d] = i;
            }
        }

        int minimumDegree = 0;
        int largestElement = 0;

        while (eliminated < n)
        {
            // ---- pick the pivot: least approximate degree, ties by index -------------
            int k = -1;
            while (minimumDegree < n && (k = listHead[minimumDegree]) == -1)
                minimumDegree++;
            if (nextInList[k] != -1)
                last[nextInList[k]] = -1;
            listHead[minimumDegree] = nextInList[k];
            int pivotElements = elementCount[k];
            int pivotSize = nodeSize[k];
            eliminated += pivotSize;

            if (pivotElements > 0 && nz + minimumDegree >= adjacency.Length)
                nz = Compact(adjacency, start, len, nz, n);

            // ---- build element Lk = the union of the pivot's nodes and elements ------
            // Every node of every element the pivot touches joins Lk exactly once
            // (nodeSize is negated as a "already in Lk" mark), and those elements are
            // then absorbed: their list is a subset of Lk, so keeping it is redundant.
            int elementDegree = 0;
            nodeSize[k] = -pivotSize;
            int scan = start[k];
            int writeStart = pivotElements == 0 ? scan : nz;   // in place when Ek is empty
            int write = writeStart;
            for (int pass = 1; pass <= pivotElements + 1; pass++)
            {
                int source, sourceLength;
                int element;
                if (pass > pivotElements)
                {
                    element = k;
                    source = scan;
                    sourceLength = len[k] - pivotElements;
                }
                else
                {
                    element = adjacency[scan++];
                    source = start[element];
                    sourceLength = len[element];
                }
                for (int step = 0; step < sourceLength; step++)
                {
                    int i = adjacency[source++];
                    int size = nodeSize[i];
                    if (size <= 0)
                        continue;   // dead, or already gathered into Lk
                    elementDegree += size;
                    nodeSize[i] = -size;
                    adjacency[write++] = i;
                    // Pull i out of its degree list; it goes back after the update.
                    if (nextInList[i] != -1)
                        last[nextInList[i]] = last[i];
                    if (last[i] != -1)
                        nextInList[last[i]] = nextInList[i];
                    else
                        listHead[degree[i]] = nextInList[i];
                }
                if (element != k)
                {
                    start[element] = Flip(k);   // absorbed: e becomes a child of k
                    work[element] = 0;
                }
            }
            if (pivotElements != 0)
                nz = write;
            degree[k] = elementDegree;
            start[k] = writeStart;
            len[k] = write - writeStart;
            elementCount[k] = -2;

            // ---- scan 1: how much of each neighbouring element sticks out of Lk ------
            // work[e] holds |Le \ Lk| offset by mark, computed by starting at degree[e]
            // and subtracting each of Lk's supervariables as it is seen inside Le.
            mark = ClearMarks(mark, largestElement, work, n);
            for (int p = writeStart; p < write; p++)
            {
                int i = adjacency[p];
                int touched = elementCount[i];
                if (touched <= 0)
                    continue;
                int size = -nodeSize[i];
                int seed = mark - size;
                for (int q = start[i]; q < start[i] + touched; q++)
                {
                    int e = adjacency[q];
                    if (work[e] >= mark)
                        work[e] -= size;
                    else if (work[e] != 0)
                        work[e] = degree[e] + seed;
                }
            }

            // ---- scan 2: the approximate degree of every node of Lk ------------------
            for (int p = writeStart; p < write; p++)
            {
                int i = adjacency[p];
                int listStart = start[i];
                int elementsEnd = listStart + elementCount[i] - 1;
                int pack = listStart;
                uint hash = 0;
                int approximate = 0;
                for (int q = listStart; q <= elementsEnd; q++)
                {
                    int e = adjacency[q];
                    if (work[e] == 0)
                        continue;   // already absorbed
                    int outside = work[e] - mark;   // |Le \ Lk|
                    if (outside > 0)
                    {
                        approximate += outside;
                        adjacency[pack++] = e;
                        hash += (uint)e;
                    }
                    else
                    {
                        // Aggressive absorption: Le is now wholly inside Lk.
                        start[e] = Flip(k);
                        work[e] = 0;
                    }
                }
                elementCount[i] = pack - listStart + 1;   // Lk itself joins Ei below

                int packedElements = pack;
                int listEnd = listStart + len[i];
                for (int q = elementsEnd + 1; q < listEnd; q++)
                {
                    int j = adjacency[q];
                    int size = nodeSize[j];
                    if (size <= 0)
                        continue;   // dead, or already inside Lk
                    approximate += size;
                    adjacency[pack++] = j;
                    hash += (uint)j;
                }

                if (approximate == 0)
                {
                    // Mass elimination: i's whole remaining adjacency is Lk, so it can be
                    // eliminated with k for free.
                    start[i] = Flip(k);
                    int size = -nodeSize[i];
                    elementDegree -= size;
                    pivotSize += size;
                    eliminated += size;
                    nodeSize[i] = 0;
                    elementCount[i] = -1;
                }
                else
                {
                    degree[i] = Math.Min(degree[i], approximate);
                    // Rotate Lk to the front of Ei: [k, e1, e2, ..., nodes...].
                    adjacency[pack] = adjacency[packedElements];
                    adjacency[packedElements] = adjacency[listStart];
                    adjacency[listStart] = k;
                    len[i] = pack - listStart + 1;
                    int bucket = (int)(hash % (uint)n);
                    nextInList[i] = hashHead[bucket];
                    hashHead[bucket] = i;
                    last[i] = bucket;
                }
            }
            degree[k] = elementDegree;
            largestElement = Math.Max(largestElement, elementDegree);
            mark = ClearMarks(mark + largestElement, largestElement, work, n);

            // ---- supervariables: merge nodes whose adjacency is now identical --------
            // The hash is a cheap filter; membership is then compared exactly by marking
            // one node's list and walking the other's.
            for (int p = writeStart; p < write; p++)
            {
                int i = adjacency[p];
                if (nodeSize[i] >= 0)
                    continue;
                int bucket = last[i];
                i = hashHead[bucket];
                hashHead[bucket] = -1;
                for (; i != -1 && nextInList[i] != -1; i = nextInList[i], mark++)
                {
                    int length = len[i];
                    int elements = elementCount[i];
                    for (int q = start[i] + 1; q < start[i] + length; q++)
                        work[adjacency[q]] = mark;
                    int previous = i;
                    for (int j = nextInList[i]; j != -1;)
                    {
                        bool identical = len[j] == length && elementCount[j] == elements;
                        for (int q = start[j] + 1; identical && q < start[j] + length; q++)
                        {
                            if (work[adjacency[q]] != mark)
                                identical = false;
                        }
                        if (identical)
                        {
                            start[j] = Flip(i);
                            nodeSize[i] += nodeSize[j];
                            nodeSize[j] = 0;
                            elementCount[j] = -1;
                            j = nextInList[j];
                            nextInList[previous] = j;
                        }
                        else
                        {
                            previous = j;
                            j = nextInList[j];
                        }
                    }
                }
            }

            // ---- put the survivors back into their degree lists ----------------------
            int compacted = writeStart;
            for (int p = writeStart; p < write; p++)
            {
                int i = adjacency[p];
                int size = -nodeSize[i];
                if (size <= 0)
                    continue;
                nodeSize[i] = size;
                int d = Math.Min(degree[i] + elementDegree - size, n - eliminated - size);
                if (listHead[d] != -1)
                    last[listHead[d]] = i;
                nextInList[i] = listHead[d];
                last[i] = -1;
                listHead[d] = i;
                minimumDegree = Math.Min(minimumDegree, d);
                degree[i] = d;
                adjacency[compacted++] = i;
            }
            nodeSize[k] = pivotSize;
            len[k] = compacted - writeStart;
            if (len[k] == 0)
            {
                start[k] = -1;
                work[k] = 0;
            }
            if (pivotElements != 0)
                nz = compacted;
        }

        // ---- postorder the assembly tree -------------------------------------------
        for (int i = 0; i < n; i++)
            start[i] = Flip(start[i]);
        for (int j = 0; j <= n; j++)
            listHead[j] = -1;
        for (int j = n; j >= 0; j--)
        {
            if (nodeSize[j] > 0)
                continue;   // an element, handled in the next sweep
            nextInList[j] = listHead[start[j]];
            listHead[start[j]] = j;
        }
        for (int e = n; e >= 0; e--)
        {
            if (nodeSize[e] <= 0 || start[e] == -1)
                continue;
            nextInList[e] = listHead[start[e]];
            listHead[start[e]] = e;
        }

        var order = new int[n + 1];
        var stack = new int[n + 1];
        for (int i = 0, count = 0; i <= n; i++)
        {
            if (start[i] == -1)
                count = TreeDepthFirst(i, count, listHead, nextInList, order, stack);
        }

        // order[n] is the virtual dense element; the first n entries are the real nodes.
        var permutation = new int[n];
        Array.Copy(order, permutation, n);
        return permutation;
    }

    /// <summary>
    /// Resets the set-difference marks when the running <paramref name="mark"/> would
    /// otherwise overflow past the largest element it has to encode. Live elements keep a
    /// nonzero entry (1) and dead ones stay at exactly 0, which is the only distinction
    /// the scans read.
    /// </summary>
    private static int ClearMarks(int mark, int largestElement, int[] work, int n)
    {
        if (mark >= 2 && mark + largestElement >= 0)
            return mark;
        for (int k = 0; k < n; k++)
        {
            if (work[k] != 0)
                work[k] = 1;
        }
        return 2;
    }

    /// <summary>
    /// Slides every live adjacency list down to the front of the arena, in place. The
    /// first entry of each list is temporarily replaced by a tagged copy of its owner's
    /// index so the sweep can tell where one list ends and the next begins.
    /// </summary>
    private static int Compact(int[] adjacency, int[] start, int[] len, int nz, int n)
    {
        for (int j = 0; j < n; j++)
        {
            int p = start[j];
            if (p < 0)
                continue;
            start[j] = adjacency[p];
            adjacency[p] = Flip(j);
        }
        int write = 0;
        for (int read = 0; read < nz;)
        {
            int j = Flip(adjacency[read++]);
            if (j < 0)
                continue;
            adjacency[write] = start[j];
            start[j] = write++;
            for (int step = 0; step < len[j] - 1; step++)
                adjacency[write++] = adjacency[read++];
        }
        return write;
    }

    /// <summary>
    /// Iterative depth-first postorder over the assembly tree, consuming each node's
    /// child list as it walks (so the traversal needs no visited marks).
    /// </summary>
    private static int TreeDepthFirst(int root, int count, int[] listHead, int[] nextInList, int[] order, int[] stack)
    {
        int top = 0;
        stack[0] = root;
        while (top >= 0)
        {
            int node = stack[top];
            int child = listHead[node];
            if (child == -1)
            {
                top--;
                order[count++] = node;
            }
            else
            {
                listHead[node] = nextInList[child];
                stack[++top] = child;
            }
        }
        return count;
    }
}
