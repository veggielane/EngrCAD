using EngrCAD.Core;
using EngrCAD.Core.Spatial;
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
/// surface patch; one that does not is named — with its coordinates and the surface tags it
/// touches — once the refinement budget runs out. The response is Ruppert's ENCROACHMENT
/// rule: red (four-way) subdivision of exactly the sub-triangles whose diametral ball
/// contains an offending vertex. Red subdivision makes every child SIMILAR to its parent, so
/// shape never degrades and circumradii halve per level, which is what makes the loop
/// converge rather than merely usually converge; and encroachment keeps the response LOCAL,
/// which is what stops one bad face cascading a whole box face per round.</para>
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

        if (options.BoundaryLayer is { } layerSpec)
        {
            return MeshWithBoundaryLayer(
                [.. positions], faces, [.. faceBody], options, layerSpec, surfaceVolume,
                out diagnostics, progress);
        }

        var builder = new Builder([.. positions], faces, [.. faceBody], windings, options, progress);
        return builder.Run(surfaceVolume, out diagnostics);
    }

    /// <summary>
    /// The layered route: march the stack, hand what is left to the ordinary pipeline, weld.
    ///
    /// <para>Nothing about the isotropic pass changes — it is called with an ordinary closed
    /// surface and an ordinary options record — which is the whole reason this is tractable
    /// and why the layer cannot weaken any guarantee the plain mesher gives.</para>
    /// </summary>
    private static TetMesh MeshWithBoundaryLayer(
        Vector3d[] positions,
        List<int[]> faces,
        int[] faceBody,
        TetMeshOptions options,
        BoundaryLayerSpec spec,
        double surfaceVolume,
        out TetMeshDiagnostics diagnostics,
        ProgressCancel? progress)
    {
        var bounds = Aabb.FromPoints(positions);
        var size = bounds.Size;
        double extent = Math.Max(size.X, Math.Max(size.Y, size.Z));

        var tags = options.FacetTags is { } supplied
            ? Enumerable.Range(0, faces.Count).Select(i => i < supplied.Count ? supplied[i] : i).ToArray()
            : faceBody;

        var builder = new BoundaryLayerBuilder(positions, faces, tags, faceBody, spec, extent);
        var core = builder.MarchAndBuildCore(progress);
        progress?.Report(0.4);

        // One combined call, so the isotropic pass sees the bodies exactly as it always does
        // and its per-element region ids line up with the stack's.
        var coreSurfaces = new List<HalfEdgeMesh>(core.Count);
        var coreTags = new List<int>();
        foreach (var body in core)
        {
            coreSurfaces.Add(body.Surface);
            // HalfEdgeMesh.Build preserves face order and the pipeline triangulates an
            // already-triangular mesh into the same order, so the tag list lines up.
            coreTags.AddRange(body.Tags);
        }

        var coreOptions = options with
        {
            BoundaryLayer = null,
            FacetTags = coreTags,
            FrozenFacetTag = builder.ReservedBase,
        };

        var filled = Mesh(coreSurfaces, coreOptions, out var coreDiagnostics, progress);
        progress?.Report(0.9);

        var mesh = builder.Complete(filled, out var layerReport);
        progress?.Report(1.0);

        double residual = Math.Abs(mesh.Volume - surfaceVolume) / Math.Max(Math.Abs(surfaceVolume), 1e-300);
        diagnostics = coreDiagnostics with
        {
            InputVertices = positions.Length,
            InputTriangles = faces.Count,
            BoundaryFacets = mesh.BoundaryFacetCount,
            InsideTets = mesh.TetCount,
            SurfaceVolume = surfaceVolume,
            VolumeResidual = residual,
            BoundaryLayer = layerReport,
            RefinementBlockedByFrozenBoundary = coreDiagnostics.RefinementBlockedByFrozenBoundary,
        };
        return mesh;
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
        private int _blockedByFrozen;
        private HashSet<(int, int)> _frozenEdges = [];

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
            // Frozen sub-triangles never split, so their edges never change: compute the set
            // once, before any refinement, and both refinement paths read the same answer.
            _frozenEdges = FrozenEdges();
            progress?.Report(0.25);

            // The BOUNDARY is sized before recovery, not after. A coarse solid's interior
            // circumcentres nearly all fall outside it (that is what makes its elements
            // coarse), so interior refinement alone stalls almost immediately: a 4-unit box
            // asked for 1.5-unit elements gained exactly one vertex and stopped at 12
            // tetrahedra. Refining the surface first gives the interior somewhere to go, and
            // doing it BEFORE recovery means recovery validates the final boundary rather
            // than one that is about to change.
            if (options.RefineQuality)
                RefineBoundaryToSize();
            progress?.Report(0.35);

            var (label, region) = Recover();
            progress?.Report(0.6);

            if (options.RefineQuality)
            {
                RefineQuality(label);
                (label, region) = Recover();
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
                LocationFallbacks: _delaunay.WalkFallbacks,
                RefinementBlockedByFrozenBoundary: _blockedByFrozen);
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

            for (int round = 0; ; round++, _recoveryRounds++)
            {
                (label, region) = Classify();
                offending = OffendingFaces(label, out _);
                if (offending.Count == 0)
                    return (label, region);

                if (round >= options.MaxRecoveryRounds)
                {
                    var worst = offending[0];
                    var nearbyTags = new SortedSet<int>();
                    foreach (int v in (int[])[worst.V0, worst.V1, worst.V2])
                        if (v < _vertexPatches.Count)
                            foreach (int p in _vertexPatches[v])
                                nearbyTags.Add(_patches[p].Tag);

                    throw new TetMeshException(
                        $"Boundary recovery did not converge after {options.MaxRecoveryRounds} rounds: " +
                        $"{offending.Count} faces separate the interior from the exterior without lying " +
                        $"on the input surface. The first spans {_points[worst.V0]} / {_points[worst.V1]} " +
                        $"/ {_points[worst.V2]}, touching surface tag(s) " +
                        $"{(nearbyTags.Count > 0 ? string.Join(", ", nearbyTags) : "none")}. A tetrahedron " +
                        "straddles the boundary there, which is usually a sliver triangle or a " +
                        "near-tangential pair of surfaces; remesh the surface (Remesher.Remesh) before " +
                        "tetrahedralizing.");
                }

                SplitEncroached(offending);
                progress?.ThrowIfCancelled();
            }
        }

        /// <summary>
        /// Refines the surface in response to faces that failed to lie on it — Ruppert's
        /// ENCROACHMENT rule: split exactly those sub-triangles whose diametral ball contains
        /// an offending vertex, and no others.
        ///
        /// <para>Locality is not an optimization here, it is the difference between
        /// converging and not. Splitting every sub-triangle of a touched PATCH — which is
        /// what this did first — quadruples a whole box face per round, so one interior
        /// insertion near a wall could cascade the entire surface; measured, it exhausted a
        /// 500 000-point budget. The encroachment ball is the smallest region that provably
        /// has to change.</para>
        /// </summary>
        private void SplitEncroached(List<BoundaryFace> offending)
        {
            var vertices = new HashSet<int>();
            foreach (var face in offending)
            {
                vertices.Add(face.V0);
                vertices.Add(face.V1);
                vertices.Add(face.V2);
            }

            var doomed = new HashSet<int>();
            for (int i = 0; i < _subTriangles.Count; i++)
            {
                var sub = _subTriangles[i];
                if (!TetGeometry.TryTriangleCircumcentre(
                        _points[sub.V0], _points[sub.V1], _points[sub.V2],
                        out var centre, out double radius))
                {
                    doomed.Add(i); // a degenerate sub-triangle cannot be recovered as it is
                    continue;
                }

                double radiusSquared = radius * radius;
                foreach (int v in vertices)
                {
                    if (v == sub.V0 || v == sub.V1 || v == sub.V2)
                        continue;
                    if ((_points[v] - centre).LengthSquared < radiusSquared)
                    {
                        doomed.Add(i);
                        break;
                    }
                }
            }

            if (doomed.Count == 0)
            {
                // Nothing is encroached, yet a face still fails to lie on the surface. Fall
                // back to splitting the patches the offending vertices belong to, which is
                // coarser but always makes progress.
                var patches = new HashSet<int>();
                foreach (int v in vertices)
                    if (v < _vertexPatches.Count)
                        patches.UnionWith(_vertexPatches[v]);
                for (int i = 0; i < _subTriangles.Count; i++)
                    if (patches.Contains(_subTriangles[i].Patch))
                        doomed.Add(i);
            }

            if (doomed.Count == 0)
                throw new TetMeshException(
                    "Boundary recovery found faces off the input surface that encroach no sub-triangle " +
                    "and belong to no surface patch, so there is nothing to refine. This indicates a " +
                    "corrupted triangulation.");

            SplitSubTriangles(doomed);
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

            // A midpoint that is ALREADY a vertex is normal, not a defect: neighbouring
            // patches refine to different levels, so a coarse triangle's edge midpoint
            // routinely lands on a finer neighbour's existing vertex (the hanging-node
            // geometry). Reusing that vertex is exactly right — it is the same point — and
            // AppendAndInsert returns its index without touching the triangulation.
            var midpoint = (_points[a] + _points[b]) * 0.5;
            bool isNew = !_delaunay.ContainsPoint(midpoint);

            int index = _delaunay.AppendAndInsert(midpoint);
            EnsureVertexPatchSlots();
            // The midpoint of an edge lies on every patch both endpoints share.
            foreach (int p in _vertexPatches[a])
                if (_vertexPatches[b].Contains(p))
                    _vertexPatches[index].Add(p);
            _vertexPatches[index].Add(patch);

            _edgeMidpoints[key] = index;
            if (isNew)
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

        /// <summary>
        /// Red-refines the surface's sub-triangles until each one's circumradius is within
        /// half the sizing field's value at its centroid. Every new vertex is an edge
        /// midpoint, so it lies on the input surface and the fidelity contract is untouched.
        /// </summary>
        private void RefineBoundaryToSize()
        {
            if (options.SizingField is null && options.MaxElementSize is null)
                return;

            for (int pass = 0; pass < 24; pass++)
            {
                var replacement = new List<SubTriangle>(_subTriangles.Count * 4);
                bool changed = false;
                foreach (var sub in _subTriangles)
                {
                    // A frozen patch is one an outer stage has already built elements against
                    // (the boundary layer's inner face). Splitting it, or anything sharing an
                    // edge with it, would put a vertex on a surface those elements already
                    // face, so the size target does not apply there.
                    if (IsFrozenOrOnItsRim(sub))
                    {
                        replacement.Add(sub);
                        continue;
                    }
                    var a = _points[sub.V0];
                    var b = _points[sub.V1];
                    var c = _points[sub.V2];
                    double target = TargetSize((a + b + c) / 3.0);
                    if (double.IsPositiveInfinity(target))
                    {
                        replacement.Add(sub);
                        continue;
                    }
                    double circumradius = TetGeometry.TriangleCircumradius(a, b, c);
                    if (circumradius <= 0.5 * target)
                    {
                        replacement.Add(sub);
                        continue;
                    }
                    Split(sub, replacement);
                    changed = true;
                }
                _subTriangles = replacement;
                if (!changed)
                    return;
                progress?.ThrowIfCancelled();
            }
        }

        private void RefineQuality(Side[] label)
        {
            for (int pass = 0; pass < 24; pass++)
            {
                // Ruppert's rule, in Ruppert's ORDER: a circumcentre that encroaches a
                // boundary sub-triangle is never inserted; the sub-triangle is split instead.
                // Inserting first and repairing the boundary afterwards is the same two
                // operations in the wrong order, and it cascades — the insertion disturbs the
                // boundary, recovery splits, the splits disturb more, and a bored plate with
                // a sizing field exhausted a 500 000-point budget. Refusing up front costs
                // one spatial query per candidate and cannot cascade at all.
                var (encroachmentIndex, balls) = BuildEncroachmentIndex();
                var encroached = new HashSet<int>();
                var hits = new List<int>();

                var candidates = new List<(double Priority, Vector3d Point, double Radius)>();
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

                    if (Encroaches(encroachmentIndex, balls, centre, hits, encroached))
                        continue; // the boundary is refined instead, below (if it is oversized)

                    candidates.Add((ratio, centre, radius));
                }

                if (encroached.Count > 0)
                {
                    SplitSubTriangles(encroached);
                    (label, _) = Classify();
                    continue;
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

                // ---- what makes this loop terminate, and the mistake that broke it ----
                //
                // Delaunay refinement terminates because a tetrahedron's circumcentre is, by
                // the empty-circumsphere property, at least its circumradius R away from
                // EVERY existing vertex. Points therefore pack no denser than the local R,
                // and a bounded domain holds boundedly many of them.
                //
                // That guarantee is against the triangulation AS IT WAS when the circumcentre
                // was computed. Inserting a batch straight off the queue silently voids it:
                // the second point of the batch has no relationship at all to the first, so
                // two candidates can land arbitrarily close together, create a sliver, and
                // feed the next round. Measured on a 4-unit box asked for 1.5-unit elements:
                // it blew through 200, 800 and 3000-point budgets without converging.
                //
                // Batching is still worth having (a pass costs a full re-classification), so
                // the fix is to keep the batch INDEPENDENT: accept a candidate only if it is
                // at least half a circumradius from every point already accepted this pass.
                // That restores the packing bound up to a constant factor.
                var accepted = new List<(Vector3d Point, double Radius)>();
                int quota = Math.Clamp(candidates.Count / 2, 1, 1024);
                foreach (var (_, point, radius) in candidates)
                {
                    if (_boundarySteiner + _qualitySteiner >= options.MaxSteinerPoints)
                        throw new TetMeshException(
                            "Quality refinement exceeded the Steiner-point budget of " +
                            $"{options.MaxSteinerPoints}. Raise TetMeshOptions.MaxSteinerPoints, relax " +
                            "RadiusEdgeRatio (values below 2.0 are not guaranteed to terminate, which is " +
                            "why the default is exactly 2.0), or coarsen the sizing field.");
                    if (_delaunay.ContainsPoint(point))
                        continue;

                    bool crowded = false;
                    foreach (var (other, otherRadius) in accepted)
                    {
                        if ((point - other).LengthSquared < 0.25 * Math.Max(radius, otherRadius)
                                                                * Math.Max(radius, otherRadius))
                        {
                            crowded = true;
                            break;
                        }
                    }
                    if (crowded)
                        continue;

                    _delaunay.AppendAndInsert(point);
                    EnsureVertexPatchSlots();
                    _qualitySteiner++;
                    accepted.Add((point, radius));
                    if (accepted.Count >= quota)
                        break;
                }

                if (accepted.Count == 0)
                    return; // every remaining candidate is unreachable; the report says so

                progress?.ThrowIfCancelled();

                // Only re-classify. Encroaching candidates were refused above, so an accepted
                // insertion cannot disturb the boundary — and Recover() costs a second
                // classification pass over every element, which at refinement scale dominated
                // the whole mesher. The boundary is re-verified ONCE after the loop, where a
                // disturbance would still be caught and repaired.
                (label, _) = Classify();
            }
        }

        /// <summary>
        /// A BVH over the surface sub-triangles' diametral balls, so "does this point
        /// encroach the boundary?" is a logarithmic query instead of a scan.
        /// </summary>
        private (Bvh Index, (Vector3d Centre, double Radius)[] Balls) BuildEncroachmentIndex()
        {
            var balls = new (Vector3d Centre, double Radius)[_subTriangles.Count];
            var boxes = new Aabb[_subTriangles.Count];
            for (int i = 0; i < _subTriangles.Count; i++)
            {
                var sub = _subTriangles[i];
                if (!TetGeometry.TryTriangleCircumcentre(
                        _points[sub.V0], _points[sub.V1], _points[sub.V2],
                        out var centre, out double radius))
                {
                    // A degenerate sub-triangle has no diametral ball; give it an empty box
                    // so it never matches, and let recovery deal with it if it ever matters.
                    balls[i] = (default, -1);
                    boxes[i] = new Aabb(new Vector3d(1, 1, 1), new Vector3d(-1, -1, -1));
                    continue;
                }
                balls[i] = (centre, radius);
                var extent = new Vector3d(radius, radius, radius);
                boxes[i] = new Aabb(centre - extent, centre + extent);
            }
            return (Bvh.Build(boxes), balls);
        }

        /// <summary>
        /// True when <paramref name="point"/> lies inside some surface sub-triangle's
        /// diametral ball — in which case it must NOT be inserted. Sub-triangles that are
        /// still oversized by the sizing field's own standard are recorded for splitting.
        ///
        /// <para><b>That size floor is what makes the loop terminate.</b> Without it, a
        /// circumcentre sitting essentially ON the surface (which is exactly what a sliver
        /// against a wall produces) encroaches whatever sub-triangles are there, splitting
        /// them forever: each split halves their diametral balls but the point stays inside,
        /// so the next pass splits again. Measured, the suite simply stopped finishing.
        /// Refusing to split a sub-triangle that already meets the target bounds the whole
        /// process by the sizing field, and the candidate is discarded instead — the quality
        /// report then says what was left behind, which is the honest outcome.</para>
        /// </summary>
        private bool Encroaches(
            Bvh index,
            (Vector3d Centre, double Radius)[] balls,
            in Vector3d point,
            List<int> hits,
            HashSet<int> encroached)
        {
            hits.Clear();
            index.Query(new Aabb(point, point), hits);
            bool any = false;
            bool frozen = false;
            foreach (int i in hits)
            {
                var (centre, radius) = balls[i];
                if (radius < 0)
                    continue;
                if ((point - centre).LengthSquared >= radius * radius)
                    continue;

                any = true;
                var sub = _subTriangles[i];
                if (IsFrozenOrOnItsRim(sub))
                {
                    // A frozen sub-triangle still BLOCKS the candidate — it has to, since
                    // Ruppert's rule is what keeps the boundary recoverable, and a point
                    // inserted essentially on the interface makes slivers recovery then cannot
                    // clear (measured: a lined duct's core produced 74 unrecoverable faces
                    // when this was let through). What it cannot do is respond by splitting,
                    // so the candidate is simply dropped and the count is REPORTED rather than
                    // the size request being quietly ignored.
                    frozen = true;
                    continue;
                }

                double target = TargetSize((_points[sub.V0] + _points[sub.V1] + _points[sub.V2]) / 3.0);
                if (!double.IsPositiveInfinity(target) && radius > 0.5 * target)
                    encroached.Add(i);
            }
            if (frozen)
                _blockedByFrozen++;   // once per CANDIDATE, so the number counts points
            return any;
        }

        /// <summary>
        /// Whether a patch is one an outer stage has already committed elements against, in
        /// which case optional refinement must leave it alone. Frozen patches are tagged at or
        /// above <see cref="TetMeshOptions.FrozenFacetTag"/> — a RANGE rather than one value,
        /// because the boundary layer gives its interface a tag per wall patch so two offset
        /// regions from different walls can never merge into one patch.
        /// </summary>
        private bool IsFrozen(int patch) =>
            options.FrozenFacetTag is { } frozen && _patches[patch].Tag >= frozen;

        /// <summary>
        /// The undirected edges of every frozen sub-triangle. A sub-triangle sharing one of
        /// them must not split either: red subdivision splits all three of its edges, and a
        /// midpoint on a frozen patch's RIM lands on the frozen patch just as surely as one in
        /// its interior. This is the case the first version missed — a duct's flat cap
        /// refining to its size target put a vertex on the layer's rim.
        /// </summary>
        private HashSet<(int, int)> FrozenEdges()
        {
            var edges = new HashSet<(int, int)>();
            if (options.FrozenFacetTag is null)
                return edges;
            foreach (var sub in _subTriangles)
            {
                if (!IsFrozen(sub.Patch))
                    continue;
                edges.Add(Edge(sub.V0, sub.V1));
                edges.Add(Edge(sub.V1, sub.V2));
                edges.Add(Edge(sub.V2, sub.V0));
            }
            return edges;

            static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        }

        /// <summary>
        /// Whether optional refinement must leave this sub-triangle alone: it is either ON a
        /// frozen patch, or it shares an EDGE with one. The second half is not a nicety — red
        /// subdivision splits all three edges, so a neighbour refining to its size target
        /// would put a vertex on the frozen patch's rim, which is on the frozen patch.
        /// </summary>
        private bool IsFrozenOrOnItsRim(in SubTriangle sub)
        {
            if (IsFrozen(sub.Patch))
                return true;
            if (_frozenEdges.Count == 0)
                return false;
            return _frozenEdges.Contains(Edge(sub.V0, sub.V1))
                || _frozenEdges.Contains(Edge(sub.V1, sub.V2))
                || _frozenEdges.Contains(Edge(sub.V2, sub.V0));

            static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        }

        private void SplitSubTriangles(HashSet<int> doomed)
        {
            var replacement = new List<SubTriangle>(_subTriangles.Count + 3 * doomed.Count);
            for (int i = 0; i < _subTriangles.Count; i++)
            {
                if (doomed.Contains(i))
                    Split(_subTriangles[i], replacement);
                else
                    replacement.Add(_subTriangles[i]);
            }
            _subTriangles = replacement;
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
/// <param name="BoundaryLayer">
/// What the boundary-layer stage did, or null when no layer was asked for. Its elements sit
/// at the front of the mesh, so <c>[0, BoundaryLayer.ElementCount)</c> names them.
/// </param>
/// <param name="RefinementBlockedByFrozenBoundary">
/// Quality-refinement points declined because they encroached a FROZEN boundary patch — the
/// boundary layer's interface, which cannot be split without leaving the stack facing half a
/// triangle. A large number means the interface triangulation, not the sizing field, is what
/// sets the element size beside the layer: refine the WALL surface before meshing. Always
/// zero without a boundary layer.
/// </param>
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
    int LocationFallbacks,
    BoundaryLayerReport? BoundaryLayer = null,
    int RefinementBlockedByFrozenBoundary = 0)
{
    /// <summary>A one-line human summary.</summary>
    public override string ToString()
    {
        string core =
            $"{InsideTets} tets from {InputTriangles} triangles / {SurfacePatches} patches / " +
            $"{InputVertices} vertices; {BoundarySteinerPoints} boundary + {QualitySteinerPoints} quality " +
            $"Steiner points, {RecoveryRounds} recovery round(s); volume residual {VolumeResidual:E2}.";
        if (RefinementBlockedByFrozenBoundary > 0)
            core += $" {RefinementBlockedByFrozenBoundary} refinement point(s) declined at the frozen " +
                    "layer interface.";
        return BoundaryLayer is { } layer ? $"{core} Boundary layer: {layer}" : core;
    }
}
