using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// Turns closed manifold surface meshes into a tetrahedral volume mesh.
///
/// <para><b>The pipeline is four stages.</b>
/// (1) Delaunay tetrahedralization of the surface's vertices
/// (<see cref="DelaunayTetrahedralization"/>, exact predicates throughout).
/// (2) <b>Classification</b> — each tetrahedron is inside iff its centroid's winding number
/// against the input surface exceeds ½ (<see cref="MeshWindingNumber"/>), which also names
/// the body it belongs to for multi-region input.
/// (3) <b>Boundary recovery</b> — the faces separating inside from outside ARE the mesh's
/// skin; recovery is the loop that refines until every one of them lies ON the input
/// surface. (4) <b>Quality refinement</b>, optional
/// (<see cref="TetMeshOptions.RefineQuality"/>).</para>
///
/// <para><b>Classification comes BEFORE the boundary, and that ordering is the whole
/// design.</b> The obvious arrangement is the reverse — recover the input triangles, then
/// flood-fill between them — and it fails twice over. First, a Delaunay triangulation picks
/// its own diagonal across a coplanar quad, so demanding the INPUT triangle never converges
/// on a box; that pushed recovery up to the planar PATCH (see <see cref="SurfacePatch"/>).
/// Second, and less obviously, an exactly-coplanar quad makes the tetrahedralization contain
/// a FLAT tetrahedron, whose four faces are both diagonals at once — so "the faces lying in
/// this patch" covers the patch TWICE and an area-coverage test reads 2.0000x and refines
/// forever. Measured on a 12x6 UV sphere: 40 of 72 patches reported exactly double area.
/// Deriving the boundary from a classification that was decided independently has neither
/// problem: a flat tetrahedron has no volume, is never kept, and its two interior-facing
/// faces fall out as the boundary with no tie to break.</para>
///
/// <para><b>Recovery is verified, not assumed.</b> Every boundary face must lie inside a
/// surface patch; one that does not is named — with its coordinates and the patch it failed
/// against — after the refinement budget runs out. Refinement is red (four-way) subdivision
/// of the offending patches' sub-triangles, so every child is SIMILAR to its parent: shape
/// never degrades and circumradii halve per level, which is what makes the loop converge
/// rather than merely usually converge.</para>
///
/// <para><b>Surface fidelity contract.</b> Boundary Steiner points are edge midpoints
/// computed in double precision, so they lie on the input surface to round-off rather than
/// exactly; the enclosed volume matches the input surface's to relative round-off (measured
/// below 1e-13 on every fixture). Each boundary facet names an input triangle via
/// <see cref="TetFacet.SourceTriangle"/> — the triangle containing its centroid, unambiguous
/// except between coplanar same-tag neighbours, where the tie goes to the lowest index and
/// the tag is the same either way.</para>
/// </summary>
public static class TetMesher
{
    /// <summary>Coplanarity tolerance for patch grouping and on-plane tests (relative to model extent).</summary>
    private const double PlaneTolerance = 1e-10;

    /// <summary>Tetrahedralizes the closed manifold surface <paramref name="surface"/>.</summary>
    public static TetMesh Mesh(
        HalfEdgeMesh surface, TetMeshOptions? options = null, ProgressCancel? progress = null) =>
        Mesh(surface, options, out _, progress);

    /// <summary>
    /// Tetrahedralizes <paramref name="surface"/> and reports what happened: Steiner counts
    /// per phase, recovery rounds, the volume identity residual, and predicate escalations.
    /// </summary>
    public static TetMesh Mesh(
        HalfEdgeMesh surface,
        TetMeshOptions? options,
        out TetMeshDiagnostics diagnostics,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return Mesh([surface], options, out diagnostics, progress);
    }

    /// <summary>
    /// Tetrahedralizes several disjoint closed bodies into ONE mesh, tagging each
    /// tetrahedron with the index of the body it fills (<see cref="TetMesh.RegionOf"/>).
    /// Bodies must not overlap; overlapping input is refused by name rather than meshed
    /// wrongly.
    /// </summary>
    public static TetMesh Mesh(
        IReadOnlyList<HalfEdgeMesh> bodies,
        TetMeshOptions? options,
        out TetMeshDiagnostics diagnostics,
        ProgressCancel? progress = null)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count == 0)
            throw new TetMeshException("TetMesher needs at least one body.");
        options ??= new TetMeshOptions();

        var positions = new List<Vector3d>();
        var faces = new List<int[]>();
        var faceBody = new List<int>();
        var windings = new List<MeshWindingNumber>();
        double surfaceVolume = 0;

        for (int b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b] ?? throw new TetMeshException($"Body {b} is null.");
            if (!body.IsClosed)
                throw new TetMeshException(
                    $"Body {b} is not CLOSED: an open shell has no inside to fill. Run " +
                    "MeshRepair.AutoRepair (or HoleFiller.FillAll) first, then mesh the result.");

            var triangles = body.Triangulated();
            double volume = triangles.Volume();
            if (volume <= 0)
                throw new TetMeshException(
                    $"Body {b} encloses a non-positive volume ({volume:G6}); its faces are wound inward. " +
                    "Flip the winding (MeshRepair.Clean re-orients per component) before meshing.");
            surfaceVolume += volume;
            windings.Add(new MeshWindingNumber(triangles));

            var (bodyPositions, bodyFaces) = triangles.ToIndexed();
            int offset = positions.Count;
            positions.AddRange(bodyPositions);
            foreach (var face in bodyFaces)
            {
                faces.Add([face[0] + offset, face[1] + offset, face[2] + offset]);
                faceBody.Add(b);
            }
        }

        var builder = new Builder([.. positions], faces, [.. faceBody], windings, options, progress);
        return builder.Run(surfaceVolume, out diagnostics);
    }

    // ------------------------------------------------------------------
    // The pipeline. One class so every stage sees the same point list and
    // the same triangulation without threading state through call chains.
    // ------------------------------------------------------------------
    private sealed class Builder(
        Vector3d[] surfacePositions,
        List<int[]> surfaceFaces,
        int[] faceBody,
        List<MeshWindingNumber> windings,
        TetMeshOptions options,
        ProgressCancel? progress)
    {
        // The triangulation owns the point list. The builder deliberately does NOT keep its
        // own copy: the triangulation appends four artificial enclosing-simplex vertices
        // after the input's, so a parallel list would agree on indices only until the first
        // Steiner point and then silently disagree forever.
        private IReadOnlyList<Vector3d> _points = surfacePositions;
        private readonly Dictionary<(int, int), int> _edgeMidpoints = [];
        private readonly List<HashSet<int>> _vertexPatches = [];
        private List<SurfacePatch> _patches = [];
        private List<SubTriangle> _subTriangles = [];
        private DelaunayTetrahedralization _delaunay = null!;
        private double _extent;
        private int _boundarySteiner;
        private int _qualitySteiner;
        private int _recoveryRounds;

        /// <summary>A piece of a patch, used only to GENERATE Steiner points during recovery —
        /// the output boundary comes from the triangulation's own faces, never from these.</summary>
        private readonly record struct SubTriangle(int V0, int V1, int V2, int Patch);

        /// <summary>A face separating an interior tetrahedron from everything else.</summary>
        private readonly record struct BoundaryFace(int Tet, int Face, int V0, int V1, int V2);

        private enum Side { Outside, Inside }

        public TetMesh Run(double surfaceVolume, out TetMeshDiagnostics diagnostics)
        {
            long escalationsBefore = Predicates3d.InSphereEscalations;

            var bounds = Aabb.FromPoints(surfacePositions);
            var size = bounds.Size;
            _extent = Math.Max(size.X, Math.Max(size.Y, size.Z));

            var tags = options.FacetTags is { } supplied
                ? Enumerable.Range(0, surfaceFaces.Count)
                    .Select(i => i < supplied.Count ? supplied[i] : i).ToArray()
                : faceBody;

            _patches = SurfacePatches.Build(surfacePositions, surfaceFaces, tags, PlaneTolerance, _extent);

            _delaunay = DelaunayTetrahedralization.Build(surfacePositions, progress);
            _points = _delaunay.Points;
            InitializeVertexPatches();
            progress?.Report(0.3);

            var (label, region) = Recover();
            progress?.Report(0.6);

            if (options.RefineQuality)
            {
                RefineQuality(label, region);
                (label, region) = Classify();
                var offending = OffendingFaces(label, out _);
                if (offending.Count > 0)
                    throw new TetMeshException(
                        $"Quality refinement broke the recovered boundary: {offending.Count} interior " +
                        "faces no longer lie on the input surface. Refinement must never move the " +
                        "boundary; this is a defect, not a configuration problem.");
            }
            progress?.Report(0.85);

            var mesh = Assemble(label, region, out int insideTets, out int outsideTets);
            progress?.Report(1.0);

            double residual = Math.Abs(mesh.Volume - surfaceVolume) / Math.Max(Math.Abs(surfaceVolume), 1e-300);
            diagnostics = new TetMeshDiagnostics(
                InputVertices: surfacePositions.Length,
                InputTriangles: surfaceFaces.Count,
                SurfacePatches: _patches.Count,
                BoundarySteinerPoints: _boundarySteiner,
                QualitySteinerPoints: _qualitySteiner,
                RecoveryRounds: _recoveryRounds,
                BoundaryFacets: mesh.BoundaryFacetCount,
                InsideTets: insideTets,
                OutsideTets: outsideTets,
                SurfaceVolume: surfaceVolume,
                VolumeResidual: residual,
                InSphereEscalations: Predicates3d.InSphereEscalations - escalationsBefore,
                LocationFallbacks: _delaunay.WalkFallbacks);
            return mesh;
        }

        // ---- vertex-to-patch membership ----

        private void InitializeVertexPatches()
        {
            for (int v = 0; v < _delaunay.VertexCount; v++)
                _vertexPatches.Add([]);

            for (int p = 0; p < _patches.Count; p++)
                foreach (int t in _patches[p].Triangles)
                {
                    var face = surfaceFaces[t];
                    _vertexPatches[face[0]].Add(p);
                    _vertexPatches[face[1]].Add(p);
                    _vertexPatches[face[2]].Add(p);
                    _subTriangles.Add(new SubTriangle(face[0], face[1], face[2], p));
                }
        }

        private void EnsureVertexPatchSlots()
        {
            while (_vertexPatches.Count < _delaunay.VertexCount)
                _vertexPatches.Add([]);
        }

        // ---- stage 2: classification ----

        /// <summary>
        /// Inside/outside per live tetrahedron, plus the body index for interior ones.
        /// A tetrahedron touching the enclosing simplex is outside by construction; an
        /// EXACTLY flat one is outside because it has no volume to fill and its centroid
        /// sits on the surface, where a winding number is meaningless. Everything else is
        /// decided by the winding number at its centroid — an oracle entirely independent of
        /// the triangulation, which is what breaks the circularity between "where is the
        /// boundary" and "which side is inside".
        /// </summary>
        private (Side[] Label, int[] Region) Classify()
        {
            var label = new Side[_delaunay.TetSlotCount];
            var region = new int[_delaunay.TetSlotCount];
            Array.Fill(region, -1);

            foreach (int t in _delaunay.LiveTets())
            {
                var tet = _delaunay.TetAt(t);
                if (_delaunay.IsArtificial(tet.A) || _delaunay.IsArtificial(tet.B)
                    || _delaunay.IsArtificial(tet.C) || _delaunay.IsArtificial(tet.D))
                    continue;

                var a = _points[tet.A];
                var b = _points[tet.B];
                var c = _points[tet.C];
                var d = _points[tet.D];
                if (Predicates3d.SignedVolume6Sign(a, b, c, d) == 0)
                    continue; // exactly flat: no volume, and its centroid lies on the surface

                var centroid = (a + b + c + d) * 0.25;
                for (int body = 0; body < windings.Count; body++)
                {
                    if (windings[body].FastWindingNumber(centroid) <= 0.5)
                        continue;
                    if (region[t] >= 0)
                        throw new TetMeshException(
                            $"The tetrahedron at {centroid} is inside both body {region[t]} and body " +
                            $"{body}; the input bodies overlap, which TetMesher does not mesh.");
                    label[t] = Side.Inside;
                    region[t] = body;
                }
            }
            return (label, region);
        }

        // ---- stage 3: boundary recovery ----

        private (Side[] Label, int[] Region) Recover()
        {
            Side[] label;
            int[] region;
            List<BoundaryFace> offending;

            for (_recoveryRounds = 0; ; _recoveryRounds++)
            {
                (label, region) = Classify();
                offending = OffendingFaces(label, out _);
                if (offending.Count == 0)
                    return (label, region);

                if (_recoveryRounds >= options.MaxRecoveryRounds)
                {
                    var worst = offending[0];
                    throw new TetMeshException(
                        $"Boundary recovery did not converge after {options.MaxRecoveryRounds} rounds: " +
                        $"{offending.Count} faces separate the interior from the exterior without lying " +
                        $"on the input surface. The first spans {_points[worst.V0]} / {_points[worst.V1]} " +
                        $"/ {_points[worst.V2]}. A tetrahedron straddles the boundary there, which is " +
                        "usually a sliver triangle or a near-tangential pair of surfaces; remesh the " +
                        "surface (Remesher.Remesh) before tetrahedralizing.");
                }

                // Refine every patch touched by an offending face's vertices. Splitting by
                // locality rather than globally is what keeps a local defect from quadrupling
                // the whole surface.
                var toSplit = new HashSet<int>();
                foreach (var face in offending)
                    foreach (int v in (int[])[face.V0, face.V1, face.V2])
                        if (v < _vertexPatches.Count)
                            toSplit.UnionWith(_vertexPatches[v]);

                if (toSplit.Count == 0)
                    throw new TetMeshException(
                        "Boundary recovery found faces off the input surface whose vertices belong to no " +
                        "surface patch, so there is nothing to refine. This indicates a corrupted " +
                        "triangulation.");

                var replacement = new List<SubTriangle>(_subTriangles.Count * 4);
                foreach (var sub in _subTriangles)
                {
                    if (toSplit.Contains(sub.Patch))
                        Split(sub, replacement);
                    else
                        replacement.Add(sub);
                }
                _subTriangles = replacement;
                progress?.ThrowIfCancelled();
            }
        }

        /// <summary>
        /// The interior/exterior separating faces that do NOT lie on the input surface, and
        /// (out) the ones that do, each already carrying its source triangle. When the first
        /// list is empty the second IS the tet mesh's boundary.
        /// </summary>
        private List<BoundaryFace> OffendingFaces(Side[] label, out List<TetFacetDraft> recovered)
        {
            var offending = new List<BoundaryFace>();
            recovered = [];
            EnsureVertexPatchSlots();

            foreach (int t in _delaunay.LiveTets())
            {
                if (label[t] != Side.Inside)
                    continue;
                for (int face = 0; face < 4; face++)
                {
                    int neighbour = _delaunay.Neighbour(t, face);
                    if (neighbour >= 0 && label[neighbour] == Side.Inside)
                        continue; // interior face

                    var (a, b, c) = _delaunay.FaceVertices(t, face);
                    if (TryOnSurface(a, b, c, out int source))
                        recovered.Add(new TetFacetDraft(t, a, b, c, source));
                    else
                        offending.Add(new BoundaryFace(t, face, a, b, c));
                }
            }
            return offending;
        }

        internal readonly record struct TetFacetDraft(int Tet, int V0, int V1, int V2, int SourceTriangle);

        /// <summary>
        /// True when the triangle (a, b, c) lies inside one of the input surface's patches.
        /// Candidates come from the vertices' patch membership, so the test is O(1) in the
        /// surface size; geometry then confirms the plane and the containment.
        /// </summary>
        private bool TryOnSurface(int a, int b, int c, out int sourceTriangle)
        {
            sourceTriangle = -1;
            if (a >= _vertexPatches.Count || b >= _vertexPatches.Count || c >= _vertexPatches.Count)
                return false;
            var pa = _vertexPatches[a];
            if (pa.Count == 0) return false;
            var pb = _vertexPatches[b];
            if (pb.Count == 0) return false;
            var pc = _vertexPatches[c];
            if (pc.Count == 0) return false;

            foreach (int patchId in pa)
            {
                if (!pb.Contains(patchId) || !pc.Contains(patchId))
                    continue;
                var patch = _patches[patchId];
                var qa = _points[a];
                var qb = _points[b];
                var qc = _points[c];

                double tol = PlaneTolerance * _extent;
                if (Math.Abs((qa - patch.Origin).Dot(patch.Normal)) > tol) continue;
                if (Math.Abs((qb - patch.Origin).Dot(patch.Normal)) > tol) continue;
                if (Math.Abs((qc - patch.Origin).Dot(patch.Normal)) > tol) continue;

                int source = ContainingTriangle(patch, (qa + qb + qc) / 3.0);
                if (source < 0)
                    continue;
                sourceTriangle = source;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The patch triangle containing <paramref name="point"/>, or -1. Ties (a point on a
        /// shared edge of two coplanar triangles) go to the lowest triangle index, which is
        /// deterministic and — since a patch never straddles two tags — carries the same tag
        /// either way.
        /// </summary>
        private int ContainingTriangle(SurfacePatch patch, in Vector3d point)
        {
            var local = patch.Frame.ToLocal(point);
            var q = new Vector2d(local.X, local.Y);
            double slack = PlaneTolerance * _extent;

            foreach (int t in patch.Triangles)
            {
                var face = surfaceFaces[t];
                var l0 = patch.Frame.ToLocal(_points[face[0]]);
                var l1 = patch.Frame.ToLocal(_points[face[1]]);
                var l2 = patch.Frame.ToLocal(_points[face[2]]);
                if (PointInTriangle2d(q, new Vector2d(l0.X, l0.Y), new Vector2d(l1.X, l1.Y),
                        new Vector2d(l2.X, l2.Y), slack))
                    return t;
            }
            return -1;
        }

        private static bool PointInTriangle2d(
            in Vector2d p, in Vector2d a, in Vector2d b, in Vector2d c, double slack)
        {
            // Orient2d returns twice an AREA, so an epsilon compared against it must carry a
            // length — the same trap the exact mesh boolean documents.
            double d0 = Predicates2d.Orient2d(a, b, p);
            double d1 = Predicates2d.Orient2d(b, c, p);
            double d2 = Predicates2d.Orient2d(c, a, p);
            double s0 = slack * (b - a).Length;
            double s1 = slack * (c - b).Length;
            double s2 = slack * (a - c).Length;

            return (d0 >= -s0 && d1 >= -s1 && d2 >= -s2)
                || (d0 <= s0 && d1 <= s1 && d2 <= s2);
        }

        /// <summary>
        /// Red (four-way) subdivision: all three edge midpoints, so every child is similar to
        /// its parent. Midpoints are memoized per vertex pair, which keeps neighbouring
        /// sub-triangles sharing ONE vertex index rather than two bit-identical duplicates —
        /// a correctness requirement, since the triangulation refuses exact duplicates.
        /// </summary>
        private void Split(in SubTriangle f, List<SubTriangle> into)
        {
            int ab = Midpoint(f.V0, f.V1, f.Patch);
            int bc = Midpoint(f.V1, f.V2, f.Patch);
            int ca = Midpoint(f.V2, f.V0, f.Patch);

            into.Add(new SubTriangle(f.V0, ab, ca, f.Patch));
            into.Add(new SubTriangle(ab, f.V1, bc, f.Patch));
            into.Add(new SubTriangle(ca, bc, f.V2, f.Patch));
            into.Add(new SubTriangle(ab, bc, ca, f.Patch));
        }

        private int Midpoint(int a, int b, int patch)
        {
            var key = a < b ? (a, b) : (b, a);
            if (_edgeMidpoints.TryGetValue(key, out int existing))
            {
                _vertexPatches[existing].Add(patch);
                return existing;
            }

            if (_boundarySteiner + _qualitySteiner >= options.MaxSteinerPoints)
                throw new TetMeshException(
                    $"Boundary recovery exceeded the Steiner-point budget of {options.MaxSteinerPoints}. " +
                    "Raise TetMeshOptions.MaxSteinerPoints, or simplify/remesh the input surface.");

            var midpoint = (_points[a] + _points[b]) * 0.5;
            if (_delaunay.ContainsPoint(midpoint))
                throw new TetMeshException(
                    $"The midpoint of edge ({a}, {b}) at {midpoint} is already a vertex of the " +
                    "triangulation, so the input surface has a vertex sitting exactly on an edge " +
                    "midpoint. Remesh the surface (Remesher.Remesh) before tetrahedralizing.");

            int index = _delaunay.AppendAndInsert(midpoint);
            EnsureVertexPatchSlots();
            // The midpoint of an edge lies on every patch both endpoints share.
            foreach (int p in _vertexPatches[a])
                if (_vertexPatches[b].Contains(p))
                    _vertexPatches[index].Add(p);
            _vertexPatches[index].Add(patch);

            _edgeMidpoints[key] = index;
            _boundarySteiner++;
            return index;
        }

        // ---- assembly ----

        private TetMesh Assemble(Side[] label, int[] region, out int insideCount, out int outsideCount)
        {
            var offending = OffendingFaces(label, out var recovered);
            if (offending.Count > 0)
                throw new TetMeshException(
                    $"{offending.Count} boundary faces do not lie on the input surface at assembly time.");

            var keep = new List<int>();
            insideCount = 0;
            outsideCount = 0;
            foreach (int t in _delaunay.LiveTets())
            {
                if (label[t] == Side.Inside)
                {
                    keep.Add(t);
                    insideCount++;
                }
                else
                {
                    outsideCount++;
                }
            }
            if (keep.Count == 0)
                throw new TetMeshException(
                    "Classification produced no interior tetrahedra: no element's centroid fell inside " +
                    "the input surface. Check that the surface is wound outward and encloses a volume.");

            var slotToOutput = new Dictionary<int, int>(keep.Count);
            var remap = new int[_delaunay.VertexCount];
            Array.Fill(remap, -1);
            var keptPositions = new List<Vector3d>();
            var tets = new int[4 * keep.Count];
            var regions = new int[keep.Count];

            for (int i = 0; i < keep.Count; i++)
            {
                slotToOutput[keep[i]] = i;
                var tet = _delaunay.TetAt(keep[i]);
                for (int j = 0; j < 4; j++)
                {
                    int v = tet[j];
                    if (remap[v] < 0)
                    {
                        remap[v] = keptPositions.Count;
                        keptPositions.Add(_points[v]);
                    }
                    tets[4 * i + j] = remap[v];
                }
                regions[i] = Math.Max(0, region[keep[i]]);
            }

            var tags = options.FacetTags;
            var facets = new List<TetFacet>(recovered.Count);
            foreach (var draft in recovered)
            {
                int tag = tags is not null && draft.SourceTriangle < tags.Count
                    ? tags[draft.SourceTriangle]
                    : draft.SourceTriangle;
                facets.Add(new TetFacet(
                    remap[draft.V0], remap[draft.V1], remap[draft.V2], slotToOutput[draft.Tet], tag));
            }

            return new TetMesh([.. keptPositions], tets, regions, [.. facets]);
        }

        // ---- stage 4: quality refinement ----

        private void RefineQuality(Side[] label, int[] region)
        {
            for (int pass = 0; pass < 60; pass++)
            {
                var candidates = new List<(double Priority, Vector3d Point)>();
                foreach (int t in _delaunay.LiveTets())
                {
                    if (label[t] != Side.Inside)
                        continue;
                    var tet = _delaunay.TetAt(t);
                    var a = _points[tet.A];
                    var b = _points[tet.B];
                    var c = _points[tet.C];
                    var d = _points[tet.D];
                    if (!TetGeometry.TryCircumcentre(a, b, c, d, out var centre, out double radius))
                        continue;

                    double shortest = TetGeometry.ShortestEdge(a, b, c, d);
                    double ratio = shortest > 0 ? radius / shortest : double.PositiveInfinity;
                    double target = TargetSize(centre);
                    bool tooBig = !double.IsPositiveInfinity(target) && radius > 0.5 * target;
                    if (ratio <= options.RadiusEdgeRatio && !tooBig)
                        continue;

                    // A circumcentre may fall OUTSIDE the domain — that is what makes a sliver
                    // a sliver. Inserting one there would push a vertex through the recovered
                    // boundary, so it is skipped: a conforming boundary is worth more than the
                    // last few badly shaped elements, and the quality report says what is left.
                    if (_delaunay.ContainsPoint(centre))
                        continue;
                    if (!IsInsideAnyBody(centre))
                        continue;

                    candidates.Add((ratio, centre));
                }

                if (candidates.Count == 0)
                    return;

                // Worst first, then by coordinates: a deterministic total order, no RNG.
                candidates.Sort(static (x, y) =>
                {
                    int byPriority = y.Priority.CompareTo(x.Priority);
                    if (byPriority != 0) return byPriority;
                    int byX = x.Point.X.CompareTo(y.Point.X);
                    if (byX != 0) return byX;
                    int byY = x.Point.Y.CompareTo(y.Point.Y);
                    return byY != 0 ? byY : x.Point.Z.CompareTo(y.Point.Z);
                });

                // Insert only a prefix: every insertion invalidates the circumcentres behind
                // it, and inserting a whole stale queue is how a refinement loop turns into an
                // over-refinement loop.
                int quota = Math.Max(1, candidates.Count / 2);
                int inserted = 0;
                foreach (var (_, point) in candidates)
                {
                    if (_boundarySteiner + _qualitySteiner >= options.MaxSteinerPoints)
                        throw new TetMeshException(
                            "Quality refinement exceeded the Steiner-point budget of " +
                            $"{options.MaxSteinerPoints}. Raise TetMeshOptions.MaxSteinerPoints, relax " +
                            "RadiusEdgeRatio, or coarsen the sizing field.");
                    if (_delaunay.ContainsPoint(point))
                        continue;
                    _delaunay.AppendAndInsert(point);
                    EnsureVertexPatchSlots();
                    _qualitySteiner++;
                    if (++inserted >= quota)
                        break;
                }

                progress?.ThrowIfCancelled();
                (label, region) = Classify();
            }
        }

        private bool IsInsideAnyBody(in Vector3d p)
        {
            foreach (var winding in windings)
                if (winding.FastWindingNumber(p) > 0.5)
                    return true;
            return false;
        }

        private double TargetSize(in Vector3d p)
        {
            double target = double.PositiveInfinity;
            if (options.SizingField is { } field)
            {
                double value = field(p);
                if (value > 0)
                    target = value;
            }
            if (options.MaxElementSize is { } cap && cap > 0)
                target = Math.Min(target, cap);
            return target;
        }
    }
}

/// <summary>
/// What <see cref="TetMesher"/> did — reported rather than logged, following the
/// <c>MeshRepair</c> / <c>ShapeHealingReport</c> convention in this codebase.
/// </summary>
/// <param name="InputVertices">Vertices in the input surface(s).</param>
/// <param name="InputTriangles">Triangles in the input surface(s) after triangulation.</param>
/// <param name="SurfacePatches">Coplanar same-tag patches the triangles grouped into.</param>
/// <param name="BoundarySteinerPoints">Points added on the boundary during recovery.</param>
/// <param name="QualitySteinerPoints">Points added in the interior during quality refinement.</param>
/// <param name="RecoveryRounds">Recovery rounds run (0 = the boundary was conforming immediately).</param>
/// <param name="BoundaryFacets">Boundary facets in the finished mesh.</param>
/// <param name="InsideTets">Tetrahedra classified inside (kept).</param>
/// <param name="OutsideTets">Tetrahedra classified outside (discarded).</param>
/// <param name="SurfaceVolume">Volume enclosed by the input surface(s).</param>
/// <param name="VolumeResidual">Relative difference between the tet mesh's volume and the surface's.</param>
/// <param name="InSphereEscalations">Exact-stage in-sphere evaluations during this mesh.</param>
/// <param name="LocationFallbacks">Point locations that fell back to an exhaustive scan.</param>
public readonly record struct TetMeshDiagnostics(
    int InputVertices,
    int InputTriangles,
    int SurfacePatches,
    int BoundarySteinerPoints,
    int QualitySteinerPoints,
    int RecoveryRounds,
    int BoundaryFacets,
    int InsideTets,
    int OutsideTets,
    double SurfaceVolume,
    double VolumeResidual,
    long InSphereEscalations,
    int LocationFallbacks)
{
    /// <summary>A one-line human summary.</summary>
    public override string ToString() =>
        $"{InsideTets} tets from {InputTriangles} triangles / {SurfacePatches} patches / " +
        $"{InputVertices} vertices; {BoundarySteinerPoints} boundary + {QualitySteinerPoints} quality " +
        $"Steiner points, {RecoveryRounds} recovery round(s); volume residual {VolumeResidual:E2}.";
}
