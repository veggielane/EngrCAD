using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// Thrown when tetrahedralization cannot proceed — a degenerate point set, an
/// unrecoverable boundary, or a refinement budget exhausted. Every message names the
/// specific element that caused it.
/// </summary>
public sealed class TetMeshException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public TetMeshException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public TetMeshException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Incremental 3D Delaunay tetrahedralization (Bowyer-Watson) over exact predicates.
///
/// <para><b>Every combinatorial decision is exact.</b> Point location reads
/// <see cref="Predicates3d.Orient3d"/>, cavity membership reads
/// <see cref="Predicates3d.InSphere"/>. Neither ever consults a tolerance, so there is no
/// epsilon anywhere in this file and no configuration that can make the topology wrong:
/// the only thing that varies with the input is how often the predicates escalate to their
/// exact stage. That is the point of paying for exact predicates — it converts a class of
/// "occasionally produces a broken mesh" failures into a cost.</para>
///
/// <para><b>Cospherical points are a TIE, not an error.</b> A CAD tessellation is full of
/// them (all eight corners of a cube lie on one sphere). <c>InSphere == 0</c> means "on the
/// circumsphere", and the cavity test uses a STRICT inequality, so such a point does not
/// invalidate the tetrahedron. The resulting triangulation is a valid Delaunay
/// triangulation — just not the unique one, which for a degenerate point set does not
/// exist. Determinism then comes from the fixed insertion order, not from the predicate.</para>
///
/// <para><b>The enclosing simplex is deliberately huge</b> (circumradius 2^10 times the
/// input's bounding-sphere radius). With inexact predicates that would be a conditioning
/// disaster; with exact ones it costs only predicate escalations, and it buys the property
/// that matters: the four artificial vertices sit far outside every circumsphere of
/// interest, so the triangulation restricted to real points is the true Delaunay
/// triangulation everywhere except near the convex hull — and the hull region is exactly
/// what inside/outside classification discards.</para>
///
/// <para>Determinism: insertion order is a fixed spatial (Morton) sort, point location is a
/// deterministic walk with a deterministic linear-scan backstop, and there is no RNG
/// anywhere. Two runs on the same input produce bit-identical output, including the order
/// of the tetrahedra.</para>
/// </summary>
internal sealed class DelaunayTetrahedralization
{
    /// <summary>Vertex indices of each tet's four faces, in OUTWARD winding for a positively
    /// oriented tet. Face i is the face opposite vertex i.</summary>
    internal static readonly int[][] FaceTable =
    [
        [1, 2, 3],
        [0, 3, 2],
        [0, 1, 3],
        [0, 2, 1],
    ];

    private readonly List<Vector3d> _points = [];
    private readonly Dictionary<Vector3d, int> _pointIndex = [];
    private readonly List<int> _tetVerts = [];    // 4 per tet
    private readonly List<int> _tetNeighbours = []; // 4 per tet; neighbour i shares face i; -1 = none
    private readonly List<bool> _dead = [];
    private readonly List<int> _freeTets = [];

    // Scratch reused across insertions so a large mesh does not re-allocate per point.
    private readonly List<int> _cavity = [];
    private readonly List<int> _cavityStack = [];
    private readonly List<(int Tet, int Face)> _cavityBoundary = [];
    private readonly List<(int F0, int F1, int F2, int OutTet, int OutFace)> _snapshot = [];
    private readonly List<int> _newTets = [];
    private readonly Dictionary<(int, int), (int Tet, int Face)> _pendingFaces = [];

    private int _firstArtificialVertex;
    private int _walkStart;

    /// <summary>Number of vertices, INCLUDING the four artificial enclosing-simplex ones.</summary>
    public int VertexCount => _points.Count;

    /// <summary>Vertex positions, including the artificial ones at the end.</summary>
    public IReadOnlyList<Vector3d> Points => _points;

    /// <summary>
    /// True when <paramref name="vertex"/> is one of the four enclosing-simplex corners.
    ///
    /// <para>Note the RANGE test rather than <c>vertex &gt;= _firstArtificialVertex</c>. The
    /// artificial corners are appended after the input points but BEFORE any Steiner point,
    /// so a "greater or equal" test calls every later insertion artificial. That bug is
    /// invisible until something actually inserts — the mesher's classification then finds
    /// no interior elements at all, because every refined tetrahedron looks like it touches
    /// the enclosing simplex.</para>
    /// </summary>
    public bool IsArtificial(int vertex) =>
        vertex >= _firstArtificialVertex && vertex < _firstArtificialVertex + 4;

    /// <summary>Capacity of the tetrahedron arrays, including dead slots.</summary>
    public int TetSlotCount => _dead.Count;

    /// <summary>True when tetrahedron slot <paramref name="tet"/> has been deleted.</summary>
    public bool IsDead(int tet) => _dead[tet];

    /// <summary>The four vertex indices of live tetrahedron <paramref name="tet"/>.</summary>
    public Tet TetAt(int tet) =>
        new(_tetVerts[4 * tet], _tetVerts[4 * tet + 1], _tetVerts[4 * tet + 2], _tetVerts[4 * tet + 3]);

    /// <summary>The neighbour across face <paramref name="face"/> of <paramref name="tet"/>, or -1.</summary>
    public int Neighbour(int tet, int face) => _tetNeighbours[4 * tet + face];

    /// <summary>Live tetrahedron slot indices, ascending.</summary>
    public IEnumerable<int> LiveTets()
    {
        for (int t = 0; t < _dead.Count; t++)
            if (!_dead[t])
                yield return t;
    }

    /// <summary>
    /// Builds the Delaunay tetrahedralization of <paramref name="points"/>. Points are
    /// inserted in a deterministic spatial (Morton) order; exactly-duplicate points are
    /// rejected by name rather than silently merged, since a duplicate in an FEA mesh is
    /// almost always a defect upstream.
    /// </summary>
    public static DelaunayTetrahedralization Build(IReadOnlyList<Vector3d> points, ProgressCancel? progress = null)
    {
        var triangulation = new DelaunayTetrahedralization();
        triangulation.Initialize(points);

        foreach (int index in MortonOrder(points))
        {
            triangulation.Insert(index);
            progress?.ThrowIfCancelled();
        }
        return triangulation;
    }

    private void Initialize(IReadOnlyList<Vector3d> points)
    {
        if (points.Count < 4)
            throw new TetMeshException(
                $"Tetrahedralization needs at least 4 points; {points.Count} given.");

        _points.AddRange(points);
        _firstArtificialVertex = points.Count;

        for (int i = 0; i < points.Count; i++)
        {
            if (!_pointIndex.TryAdd(points[i], i))
                throw new TetMeshException(
                    $"Points {_pointIndex[points[i]]} and {i} are exactly coincident at {points[i]}. " +
                    "Weld the input surface (MeshRepair.Clean) before meshing.");
        }

        var bounds = Aabb.Empty;
        foreach (var p in points)
            bounds = bounds.Union(p);
        var centre = bounds.Center;
        double radius = (bounds.Max - centre).Length;
        if (!(radius > 0))
            throw new TetMeshException("All input points are coincident; there is nothing to tetrahedralize.");

        // A regular tetrahedron about the centre with circumradius 2^10 * radius. The
        // power-of-two factor keeps the corner coordinates' mantissas identical to the
        // pattern's, so the enclosing simplex is reproducible bit-for-bit at any model scale.
        double r = radius * 1024.0;
        Span<Vector3d> pattern =
        [
            new(1, 1, 1),
            new(1, -1, -1),
            new(-1, 1, -1),
            new(-1, -1, 1),
        ];
        double norm = r / Math.Sqrt(3.0);
        foreach (ref readonly var direction in pattern)
            _points.Add(centre + direction * norm);

        int s0 = _firstArtificialVertex, s1 = s0 + 1, s2 = s0 + 2, s3 = s0 + 3;
        // Order the corners so the seed tetrahedron is positively oriented.
        if (Predicates3d.SignedVolume6Sign(_points[s0], _points[s1], _points[s2], _points[s3]) < 0)
            (s1, s2) = (s2, s1);

        _walkStart = CreateTet(s0, s1, s2, s3);
        for (int face = 0; face < 4; face++)
            _tetNeighbours[4 * _walkStart + face] = -1;
    }

    private int CreateTet(int a, int b, int c, int d)
    {
        int index;
        if (_freeTets.Count > 0)
        {
            index = _freeTets[^1];
            _freeTets.RemoveAt(_freeTets.Count - 1);
            _dead[index] = false;
            _tetVerts[4 * index] = a;
            _tetVerts[4 * index + 1] = b;
            _tetVerts[4 * index + 2] = c;
            _tetVerts[4 * index + 3] = d;
            for (int i = 0; i < 4; i++)
                _tetNeighbours[4 * index + i] = -1;
        }
        else
        {
            index = _dead.Count;
            _dead.Add(false);
            _tetVerts.AddRange([a, b, c, d]);
            _tetNeighbours.AddRange([-1, -1, -1, -1]);
        }
        return index;
    }

    // ---- insertion ----

    /// <summary>Inserts vertex <paramref name="vertex"/> (an index into <see cref="Points"/>).</summary>
    public void Insert(int vertex)
    {
        var p = _points[vertex];
        int containing = LocateTet(p);
        CollectCavity(containing, p);
        Retriangulate(vertex);
    }

    /// <summary>
    /// Appends <paramref name="point"/> to the vertex list and inserts it, returning its
    /// index. A point exactly equal to an existing vertex returns that vertex's index and
    /// changes nothing — refinement legitimately proposes a point that is already there,
    /// and inserting a duplicate would build a degenerate cavity.
    /// </summary>
    public int AppendAndInsert(in Vector3d point)
    {
        if (_pointIndex.TryGetValue(point, out int existing))
            return existing;

        int index = _points.Count;
        _points.Add(point);
        _pointIndex[point] = index;
        Insert(index);
        return index;
    }

    /// <summary>True when <paramref name="point"/> is already a vertex of the triangulation.</summary>
    public bool ContainsPoint(in Vector3d point) => _pointIndex.ContainsKey(point);

    /// <summary>
    /// The live tetrahedron whose closed region contains <paramref name="point"/>. Public
    /// for classification, which needs to know which side of the domain a candidate
    /// refinement point falls on.
    /// </summary>
    public int Locate(in Vector3d point) => LocateTet(point);

    /// <summary>
    /// Finds a live tetrahedron whose closed region contains <paramref name="p"/>, by a
    /// deterministic straight walk from the last insertion's result.
    ///
    /// <para>A visibility walk can in principle cycle on degenerate configurations. Rather
    /// than perturbing or randomizing (which would cost determinism), the walk carries a
    /// step budget and falls back to an exhaustive scan — a slower way to compute exactly
    /// the same answer, which is the only kind of fallback worth having. <see cref="WalkFallbacks"/>
    /// counts them so an unexpected rate is visible rather than merely slow.</para>
    /// </summary>
    private int LocateTet(in Vector3d p)
    {
        int current = _walkStart;
        if (_dead[current])
            current = FirstLiveTet();

        int budget = 8 + 4 * _dead.Count;
        for (int step = 0; step < budget; step++)
        {
            int exitFace = -1;
            for (int face = 0; face < 4; face++)
            {
                int neighbour = _tetNeighbours[4 * current + face];
                if (neighbour < 0)
                    continue;
                var (f0, f1, f2) = FaceVertices(current, face);
                // Faces are wound outward, so a point strictly outside face `face` has a
                // POSITIVE signed volume against it.
                if (Predicates3d.SignedVolume6Sign(_points[f0], _points[f1], _points[f2], p) > 0)
                {
                    exitFace = face;
                    break;
                }
            }
            if (exitFace < 0)
            {
                _walkStart = current;
                return current;
            }
            current = _tetNeighbours[4 * current + exitFace];
        }

        WalkFallbacks++;
        foreach (int t in LiveTets())
        {
            bool inside = true;
            for (int face = 0; face < 4 && inside; face++)
            {
                var (f0, f1, f2) = FaceVertices(t, face);
                if (Predicates3d.SignedVolume6Sign(_points[f0], _points[f1], _points[f2], p) > 0)
                    inside = false;
            }
            if (inside)
            {
                _walkStart = t;
                return t;
            }
        }

        throw new TetMeshException(
            $"Point location failed for {p}: it lies outside the enclosing simplex, which cannot happen " +
            "for a point that was part of the input bounds. This indicates a corrupted triangulation.");
    }

    /// <summary>How many point locations fell back to an exhaustive scan. Diagnostic only.</summary>
    public int WalkFallbacks { get; private set; }

    private int FirstLiveTet()
    {
        for (int t = 0; t < _dead.Count; t++)
            if (!_dead[t])
                return t;
        throw new TetMeshException("The triangulation contains no live tetrahedra.");
    }

    internal (int, int, int) FaceVertices(int tet, int face)
    {
        var table = FaceTable[face];
        int b = 4 * tet;
        return (_tetVerts[b + table[0]], _tetVerts[b + table[1]], _tetVerts[b + table[2]]);
    }

    /// <summary>
    /// True when <paramref name="p"/> lies STRICTLY inside tetrahedron <paramref name="tet"/>'s
    /// circumsphere. Tets are stored positively oriented, i.e. <c>Orient3d &lt; 0</c>, which
    /// inverts <see cref="Predicates3d.InSphere"/>'s documented sign — the standing trap this
    /// one-line helper exists to contain.
    /// </summary>
    private bool InCircumsphere(int tet, in Vector3d p)
    {
        var t = TetAt(tet);
        return Predicates3d.InSphere(_points[t.A], _points[t.B], _points[t.C], _points[t.D], p) < 0;
    }

    private void CollectCavity(int seed, in Vector3d p)
    {
        _cavity.Clear();
        _cavityStack.Clear();
        _cavityBoundary.Clear();

        var inCavity = new HashSet<int> { seed };
        _cavity.Add(seed);
        _cavityStack.Add(seed);

        while (_cavityStack.Count > 0)
        {
            int tet = _cavityStack[^1];
            _cavityStack.RemoveAt(_cavityStack.Count - 1);

            for (int face = 0; face < 4; face++)
            {
                int neighbour = _tetNeighbours[4 * tet + face];
                if (neighbour < 0)
                {
                    _cavityBoundary.Add((tet, face));
                    continue;
                }
                if (inCavity.Contains(neighbour))
                    continue;
                if (InCircumsphere(neighbour, p))
                {
                    inCavity.Add(neighbour);
                    _cavity.Add(neighbour);
                    _cavityStack.Add(neighbour);
                }
                else
                {
                    _cavityBoundary.Add((tet, face));
                }
            }
        }
    }

    private void Retriangulate(int vertex)
    {
        var p = _points[vertex];

        // Snapshot each cavity-boundary face BEFORE anything is deleted. This is not
        // defensive tidiness: CreateTet recycles the slots of the cavity tets it just
        // freed, so reading a cavity tet's vertices during the creation loop returns the
        // NEW tet's vertices. That defect produced a cavity whose internal faces refused to
        // pair up, reported (correctly, but one layer too late) as "not a topological ball".
        _snapshot.Clear();
        foreach (var (tet, face) in _cavityBoundary)
        {
            var table = FaceTable[face];
            int b = 4 * tet;
            int neighbour = _tetNeighbours[b + face];
            _snapshot.Add((
                _tetVerts[b + table[0]], _tetVerts[b + table[1]], _tetVerts[b + table[2]],
                neighbour,
                neighbour < 0 ? -1 : NeighbourFaceIndex(neighbour, tet)));
        }

        foreach (int tet in _cavity)
        {
            _dead[tet] = true;
            _freeTets.Add(tet);
        }

        _newTets.Clear();
        _pendingFaces.Clear();

        foreach (var (f0, f1, f2, outTet, outFace) in _snapshot)
        {
            // The face is wound outward from the cavity, so p sits on its negative side and
            // (f0, f1, f2, p) is negatively oriented; swapping f1 and f2 makes it positive.
            // With that layout, face 3 of the new tet (opposite p) IS the original face.
            int created = CreateTet(f0, f2, f1, vertex);
            _newTets.Add(created);

            _tetNeighbours[4 * created + 3] = outTet;
            if (outTet >= 0)
                _tetNeighbours[4 * outTet + outFace] = created;

            // Faces 0..2 all contain p; each is identified by the unordered pair of its two
            // other vertices, and every such pair occurs exactly twice across the cavity's
            // boundary — once per face sharing that edge. Matching on the EDGE rather than
            // on the triangle avoids sorting a triple per face.
            for (int newFace = 0; newFace < 3; newFace++)
            {
                var (a, bb, c) = FaceVertices(created, newFace);
                // Faces 0..2 each contain `vertex` exactly once; the other two vertices are
                // the shared edge that identifies this face to its cavity neighbour.
                int u, v;
                if (a == vertex) (u, v) = (bb, c);
                else if (bb == vertex) (u, v) = (a, c);
                else (u, v) = (a, bb);

                var key = u < v ? (u, v) : (v, u);
                if (_pendingFaces.Remove(key, out var partner))
                {
                    _tetNeighbours[4 * created + newFace] = partner.Tet;
                    _tetNeighbours[4 * partner.Tet + partner.Face] = created;
                }
                else
                {
                    _pendingFaces[key] = (created, newFace);
                }
            }
        }

        if (_pendingFaces.Count > 0)
            throw new TetMeshException(
                $"Cavity retriangulation left {_pendingFaces.Count} unmatched internal faces while inserting " +
                $"vertex {vertex} at {p}. The Bowyer-Watson cavity was not a topological ball, which means " +
                "the triangulation was already corrupt.");

        _walkStart = _newTets[0];
    }

    private int NeighbourFaceIndex(int tet, int wanted)
    {
        for (int face = 0; face < 4; face++)
            if (_tetNeighbours[4 * tet + face] == wanted)
                return face;
        throw new TetMeshException($"Tetrahedron {tet} does not list {wanted} as a neighbour; adjacency is corrupt.");
    }

    // ---- validation ----

    /// <summary>
    /// Structural self-check: positive orientation everywhere, symmetric adjacency, and the
    /// Delaunay property (no live tetrahedron's circumsphere strictly contains another
    /// vertex). O(n^2) in the last clause, so it is a test tool rather than a pipeline step.
    /// </summary>
    public void Validate(bool checkDelaunay = true)
    {
        foreach (int t in LiveTets())
        {
            var tet = TetAt(t);
            if (Predicates3d.SignedVolume6Sign(_points[tet.A], _points[tet.B], _points[tet.C], _points[tet.D]) <= 0)
                throw new TetMeshException($"Tetrahedron {t} is degenerate or negatively oriented.");

            for (int face = 0; face < 4; face++)
            {
                int neighbour = _tetNeighbours[4 * t + face];
                if (neighbour < 0)
                    continue;
                if (_dead[neighbour])
                    throw new TetMeshException($"Tetrahedron {t} face {face} points at dead tetrahedron {neighbour}.");
                if (NeighbourFaceIndex(neighbour, t) < 0)
                    throw new TetMeshException($"Adjacency between {t} and {neighbour} is not symmetric.");

                var (a, b, c) = FaceVertices(t, face);
                var (x, y, z) = FaceVertices(neighbour, NeighbourFaceIndex(neighbour, t));
                var mine = new[] { a, b, c }.Order().ToArray();
                var theirs = new[] { x, y, z }.Order().ToArray();
                if (!mine.SequenceEqual(theirs))
                    throw new TetMeshException($"Tetrahedra {t} and {neighbour} claim adjacency across different faces.");
            }
        }

        if (!checkDelaunay)
            return;

        foreach (int t in LiveTets())
        {
            var tet = TetAt(t);
            for (int v = 0; v < _points.Count; v++)
            {
                if (v == tet.A || v == tet.B || v == tet.C || v == tet.D)
                    continue;
                if (InCircumsphere(t, _points[v]))
                    throw new TetMeshException(
                        $"Delaunay property violated: vertex {v} lies inside tetrahedron {t}'s circumsphere.");
            }
        }
    }

    // ---- deterministic spatial ordering ----

    /// <summary>
    /// Indices in Morton (Z-curve) order of the points, quantized to a 21-bit grid per axis.
    /// A spatial insertion order keeps the location walk short (successive points are close,
    /// so the walk is O(1) amortized instead of O(n^(1/3))); it is a fixed function of the
    /// coordinates, so it costs nothing in determinism. Ties (points sharing a cell) fall
    /// back to input index, which keeps the order a total one.
    /// </summary>
    internal static int[] MortonOrder(IReadOnlyList<Vector3d> points)
    {
        var bounds = Aabb.Empty;
        foreach (var p in points)
            bounds = bounds.Union(p);
        var size = bounds.Size;
        double extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        double scale = extent > 0 ? (double)((1 << 21) - 1) / extent : 0.0;

        var keys = new ulong[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var d = points[i] - bounds.Min;
            keys[i] = Morton3(
                (uint)Math.Clamp(d.X * scale, 0, (1 << 21) - 1),
                (uint)Math.Clamp(d.Y * scale, 0, (1 << 21) - 1),
                (uint)Math.Clamp(d.Z * scale, 0, (1 << 21) - 1));
        }

        var order = new int[points.Count];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b));
        return order;
    }

    private static ulong Morton3(uint x, uint y, uint z) =>
        Part1By2(x) | (Part1By2(y) << 1) | (Part1By2(z) << 2);

    /// <summary>Spreads the low 21 bits of x so two zero bits sit between consecutive bits.</summary>
    private static ulong Part1By2(uint value)
    {
        ulong x = value & 0x1FFFFFul;
        x = (x | (x << 32)) & 0x1F00000000FFFFul;
        x = (x | (x << 16)) & 0x1F0000FF0000FFul;
        x = (x | (x << 8)) & 0x100F00F00F00F00Ful;
        x = (x | (x << 4)) & 0x10C30C30C30C30C3ul;
        x = (x | (x << 2)) & 0x1249249249249249ul;
        return x;
    }
}
