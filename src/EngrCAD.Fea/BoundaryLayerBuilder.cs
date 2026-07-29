using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Fea;

/// <summary>
/// Builds the graded layer stack and the trimmed surface the isotropic mesher then fills.
///
/// <para><b>The whole design rests on one observation</b>: if the stack's NODES are marched
/// first, what is left over is bounded by an ordinary closed triangle mesh — the offset wall
/// plus the trimmed non-wall faces — which <see cref="TetMesher"/> already knows how to fill.
/// So there is no new volume algorithm here at all: this stage produces a surface and a set
/// of columns, and the existing pipeline does the rest.</para>
///
/// <para><b>The stage runs in TWO passes, and the reason is the mesher's own design.</b>
/// Boundary recovery works per planar PATCH, not per input triangle, precisely because a
/// Delaunay triangulation is free to pick its own diagonal across a coplanar quad — so
/// handing the fill an offset wall and assuming it comes back triangulated the same way is
/// wrong on the most ordinary geometry there is. It is wrong SILENTLY in the worst way, too:
/// the two halves of the interface would differ by one diagonal and every solver would
/// integrate over a gap. Hence: march the columns, hand over the surface, then read the
/// interface triangulation BACK off the finished fill and build the prisms on THAT. The fill
/// chooses; the stack conforms. Nothing else can make the interface agree, because nothing
/// else asks the party that decides.</para>
///
/// <para><b>Prisms are split into tetrahedra, and the diagonal rule is combinatorial.</b> A
/// <see cref="TetMesh"/> stores tetrahedra, so the prisms have to be split, and each of a
/// prism's three quadrilateral side faces needs a diagonal. Two prisms sharing a quad must
/// pick the SAME diagonal or the mesh is non-conforming again. The rule here is Dompierre's:
/// a quad's diagonal contains whichever of its two base vertices has the smaller index in the
/// INPUT SURFACE. It is symmetric in the two vertices, so both neighbours reach the same
/// answer without communicating, and it holds across layers too.
/// <b>The geometric rule this repo uses elsewhere (<c>PolygonFan</c>'s shorter diagonal) would
/// be wrong here</b>, and not marginally: a layer quad on a flat wall is an exact rectangle,
/// whose two diagonals are mathematically equal, so the choice would fall to round-off on
/// essentially every element of the stack — the same trap that made 408 of a UV sphere's 960
/// quads flip on an ulp.</para>
/// </summary>
internal sealed class BoundaryLayerBuilder
{
    /// <summary>How nearly parallel two plane normals must be to count as the same constraint.</summary>
    private const double DirectionTolerance = 1e-9;

    /// <summary>Coplanarity tolerance for patch grouping, matching <see cref="TetMesher"/>'s.</summary>
    private const double PlaneTolerance = 1e-10;

    private readonly Vector3d[] _surface;
    private readonly IReadOnlyList<int[]> _faces;
    private readonly IReadOnlyList<int> _tags;
    private readonly int[] _faceBody;
    private readonly BoundaryLayerSpec _spec;
    private readonly double _extent;
    private readonly IReadOnlyList<double> _thicknesses;

    private readonly bool[] _isWallFace;
    private readonly int[] _wallFaces;
    private readonly bool[] _isWallVertex;

    private readonly List<SurfacePatch> _wallPatches;
    private readonly HashSet<int>[] _vertexWallPatches;

    /// <summary>Node positions: the input surface's vertices first, then every marched level.</summary>
    private readonly List<Vector3d> _nodes;

    /// <summary>Base surface vertex of each node, or -1 for a node the fill invented.</summary>
    private readonly List<int> _baseOfNode;

    /// <summary>Marching level of each node (0 on the wall, n at the interface), or -1.</summary>
    private readonly List<int> _levelOfNode;

    /// <summary>_columns[v] is null for a non-wall vertex, otherwise levels 0..n of its column.</summary>
    private readonly int[]?[] _columns;

    private readonly List<Vector3d>?[] _constraints;

    private readonly List<int> _tets = [];
    private readonly List<int> _regions = [];

    /// <summary>Sorted node triple -> tag, for the faces the stack itself exposes.</summary>
    private readonly Dictionary<(int, int, int), int> _exposedTags = [];

    private int _junctionNodes;
    private int _retriangulatedPatches;
    private double _minClearance = double.PositiveInfinity;

    /// <summary>
    /// The first tag reserved for the interface. Offset triangles carry
    /// <c>ReservedBase + wallPatchIndex</c>, which does three jobs at once: it can never
    /// collide with a caller's tag, it keeps two offset regions from different wall patches
    /// from merging into one patch, and it is how the second pass recognises the interface
    /// facets and recovers which wall they came from.
    /// </summary>
    internal int ReservedBase { get; }

    internal BoundaryLayerBuilder(
        Vector3d[] surface,
        IReadOnlyList<int[]> faces,
        IReadOnlyList<int> tags,
        int[] faceBody,
        BoundaryLayerSpec spec,
        double extent)
    {
        _surface = surface;
        _faces = faces;
        _tags = tags;
        _faceBody = faceBody;
        _spec = spec;
        _extent = extent;

        Validate();
        _thicknesses = spec.Thicknesses;

        _isWallFace = new bool[faces.Count];
        _isWallVertex = new bool[surface.Length];
        var wall = new List<int>();
        var wallTriangles = new List<int[]>();
        var wallTags = new List<int>();
        for (int f = 0; f < faces.Count; f++)
        {
            var (centroid, normal, area) = FaceGeometry(f);
            if (!spec.Wall(new FacetRef(f, tags[f], centroid, normal, area)))
                continue;
            if (!(area > 0))
                throw new TetMeshException(
                    $"The boundary-layer wall selection includes the degenerate input triangle {f} " +
                    $"(tag {tags[f]}, centroid {centroid}), which has no normal to march along. Clean the " +
                    "surface (MeshRepair.Clean removes degenerate faces) before meshing.");
            _isWallFace[f] = true;
            wall.Add(f);
            wallTriangles.Add(faces[f]);
            wallTags.Add(tags[f]);
            foreach (int v in faces[f])
                _isWallVertex[v] = true;
        }
        _wallFaces = [.. wall];

        if (_wallFaces.Length == 0)
            throw new TetMeshException(
                "The boundary-layer spec selected no wall facets. Check the Wall predicate against the " +
                "tags actually present (TetMeshOptions.FacetTags entries, or raw input triangle indices " +
                "when no tags were supplied); a layer that selects nothing is a modelling mistake, not a " +
                "request for no layer.");

        _wallPatches = SurfacePatches.Build(surface, wallTriangles, wallTags, PlaneTolerance, extent);
        _vertexWallPatches = new HashSet<int>[surface.Length];
        for (int p = 0; p < _wallPatches.Count; p++)
            foreach (int local in _wallPatches[p].Triangles)
                foreach (int v in wallTriangles[local])
                    (_vertexWallPatches[v] ??= []).Add(p);

        int highest = int.MinValue;
        foreach (int tag in tags)
            highest = Math.Max(highest, tag);
        ReservedBase = highest == int.MinValue ? 0 : highest + 1;

        _nodes = [.. surface];
        _baseOfNode = new List<int>(surface.Length);
        _levelOfNode = new List<int>(surface.Length);
        for (int v = 0; v < surface.Length; v++)
        {
            _baseOfNode.Add(v);
            _levelOfNode.Add(0);
        }
        _columns = new int[]?[surface.Length];
        _constraints = new List<Vector3d>?[surface.Length];
    }

    private void Validate()
    {
        if (_spec.LayerCount < 1)
            throw new TetMeshException(
                $"BoundaryLayerSpec.LayerCount is {_spec.LayerCount}; a layer stack needs at least one layer.");
        if (!(_spec.FirstLayerThickness > 0))
            throw new TetMeshException(
                $"BoundaryLayerSpec.FirstLayerThickness is {_spec.FirstLayerThickness:G6}; it must be positive.");
        if (!(_spec.GrowthRatio > 0))
            throw new TetMeshException(
                $"BoundaryLayerSpec.GrowthRatio is {_spec.GrowthRatio:G6}; it must be positive.");
        if (_spec.DirectionSmoothingPasses < 0)
            throw new TetMeshException(
                $"BoundaryLayerSpec.DirectionSmoothingPasses is {_spec.DirectionSmoothingPasses}; " +
                "it cannot be negative.");
        if (!(_spec.MinimumConstraintCosine > 0) || _spec.MinimumConstraintCosine > 1)
            throw new TetMeshException(
                $"BoundaryLayerSpec.MinimumConstraintCosine is {_spec.MinimumConstraintCosine:G6}; " +
                "it must lie in (0, 1].");
    }

    private (Vector3d Centroid, Vector3d Normal, double Area) FaceGeometry(int f)
    {
        var t = _faces[f];
        var a = _surface[t[0]];
        var b = _surface[t[1]];
        var c = _surface[t[2]];
        var raw = (b - a).Cross(c - a);
        double twiceArea = raw.Length;
        var normal = twiceArea > 0 ? raw / twiceArea : default;
        return ((a + b + c) / 3.0, normal, 0.5 * twiceArea);
    }

    // ==================================================================
    // Pass 1: march, and hand over the surface bounding what is left.
    // ==================================================================

    /// <summary>One body's remaining void, as a closed surface plus per-triangle tags.</summary>
    internal sealed record CoreSurface(HalfEdgeMesh Surface, int[] Tags, int Body);

    internal List<CoreSurface> MarchAndBuildCore(ProgressCancel? progress)
    {
        var directions = MarchDirections();
        progress?.ThrowIfCancelled();

        MarchColumns(directions);
        CheckFolding();
        MeasureClearance();
        progress?.ThrowIfCancelled();

        var core = BuildCoreSurfaces();

        // The exact statement of "the layers do not eat each other", and it runs BEFORE the
        // volume test on purpose: a stack that swallows its body has a self-crossing surface
        // too, and "the layers cross here" names what to change where "nothing is left"
        // only says the outcome. A local fold test cannot see two walls closing on one
        // another from opposite sides — every element stays perfectly well shaped right up to
        // the moment the two offset sheets pass through each other — so the test has to be
        // the global one, and the mesh engine already owns it.
        foreach (var body in core)
        {
            // Deliberately NOT given the ProgressCancel: it reports its own 0..1 fraction,
            // which would make the mesher's overall progress jump to complete and back.
            progress?.ThrowIfCancelled();
            var report = MeshIntersection.WithinItself(body.Surface);
            if (!report.Crosses)
                continue;
            var segment = report.Segments.First(s => s.Transversal);
            throw new TetMeshException(
                $"The boundary layer self-intersects: after marching {_spec.TotalThickness:G6} over " +
                $"{_spec.LayerCount} layer(s), the surface left over crosses itself near " +
                $"{segment.Start} (total crossing length {report.CurveLength:G4}). Two walls have closed " +
                "on each other, or the wall turns tighter than the stack is tall. Reduce LayerCount or " +
                "the layer thicknesses; a stack has to fit in the passage it is lining, and inverted " +
                "elements there would be far worse than this refusal.");
        }

        foreach (var body in core)
        {
            if (!(body.Surface.Volume() > 0))
                throw new TetMeshException(
                    $"The boundary layer consumed body {body.Body}: what is left encloses " +
                    $"{body.Surface.Volume():G6}, so there is no volume for the isotropic fill. Reduce " +
                    "LayerCount or the layer thicknesses.");
        }
        return core;
    }

    // ---- marching directions ----

    /// <summary>
    /// The inward direction at every wall vertex: the ANGLE-WEIGHTED average of its incident
    /// wall faces' normals, negated.
    ///
    /// <para><b>It has to be a per-node average and not the facet normal.</b> Marching each
    /// triangle along its own normal moves a shared vertex to several different places, so
    /// the layer tears open along every edge where two facets disagree — which on a curved
    /// wall is every edge. Angle weighting rather than area weighting is this codebase's
    /// existing convention for a vertex normal (<c>MeshSdf</c>'s pseudonormal), and it is the
    /// better one here too: it depends only on the surface's shape at the vertex, so refining
    /// one neighbouring triangle does not tilt the direction the way an area-weighted average
    /// would.</para>
    /// </summary>
    private Vector3d[] MarchDirections()
    {
        var directions = new Vector3d[_surface.Length];
        foreach (int f in _wallFaces)
        {
            var t = _faces[f];
            for (int i = 0; i < 3; i++)
            {
                var p = _surface[t[i]];
                var u = _surface[t[(i + 1) % 3]] - p;
                var v = _surface[t[(i + 2) % 3]] - p;
                var raw = u.Cross(v);
                double twiceArea = raw.Length;
                if (twiceArea <= 0)
                    continue;
                // atan2 of the cross and dot magnitudes: exact at any vector length, with no
                // normalization and no epsilon (TetGeometry.DihedralAngles' rule).
                double angle = Math.Atan2(twiceArea, u.Dot(v));
                directions[t[i]] += raw / twiceArea * angle;
            }
        }

        for (int v = 0; v < directions.Length; v++)
        {
            if (!_isWallVertex[v])
                continue;
            double length = directions[v].Length;
            if (!(length > 0))
                throw new TetMeshException(
                    $"The wall vertex at {_surface[v]} has no marching direction: its incident wall " +
                    "facets' normals cancel exactly, which means the wall doubles back on itself there " +
                    "(a knife edge, or two coincident facets with opposite winding). Repair the surface " +
                    "or exclude that face from the wall selection.");
            directions[v] = -(directions[v] / length);   // inward
        }

        CollectJunctionConstraints();
        for (int pass = 0; pass < _spec.DirectionSmoothingPasses; pass++)
        {
            directions = SmoothDirections(directions);
            ApplyJunctionConstraints(directions, refusing: false);
        }
        ApplyJunctionConstraints(directions, refusing: true);
        return directions;
    }

    /// <summary>One Laplacian pass over the wall's own edge graph, wall vertices only.</summary>
    private Vector3d[] SmoothDirections(Vector3d[] directions)
    {
        var sum = new Vector3d[directions.Length];
        var count = new int[directions.Length];
        foreach (int f in _wallFaces)
        {
            var t = _faces[f];
            for (int i = 0; i < 3; i++)
            {
                int a = t[i], b = t[(i + 1) % 3];
                sum[a] += directions[b];
                count[a]++;
                sum[b] += directions[a];
                count[b]++;
            }
        }

        var next = new Vector3d[directions.Length];
        for (int v = 0; v < directions.Length; v++)
        {
            if (!_isWallVertex[v] || count[v] == 0)
            {
                next[v] = directions[v];
                continue;
            }
            var blended = directions[v] + sum[v] / count[v];
            double length = blended.Length;
            next[v] = length > 0 ? blended / length : directions[v];
        }
        return next;
    }

    /// <summary>The distinct non-wall plane normals at each wall vertex.</summary>
    private void CollectJunctionConstraints()
    {
        for (int f = 0; f < _faces.Count; f++)
        {
            if (_isWallFace[f])
                continue;
            var (_, normal, area) = FaceGeometry(f);
            if (area <= 0)
                continue;
            foreach (int v in _faces[f])
            {
                if (!_isWallVertex[v])
                    continue;   // nothing marches from here
                var list = _constraints[v] ??= [];
                bool known = false;
                foreach (var n in list)
                {
                    if (Math.Abs(n.Dot(normal)) >= 1 - DirectionTolerance)
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                    list.Add(normal);
            }
        }

        for (int v = 0; v < _surface.Length; v++)
        {
            var list = _constraints[v];
            if (list is null || !_isWallVertex[v])
                continue;
            _junctionNodes++;
            if (list.Count > 2)
                throw new TetMeshException(
                    $"The boundary layer's rim vertex at {_surface[v]} touches {list.Count} distinct " +
                    "non-wall planes, so its column has nowhere to slide. Either the non-wall surface " +
                    "meeting the wall there is CURVED (a layer rim can only slide along flat faces in " +
                    "this version), or the wall ends in a corner. Extend the wall selection over the " +
                    "curved neighbour, or stop the wall at a flat face.");
        }
    }

    /// <summary>
    /// A wall vertex that also belongs to NON-wall faces sits on the rim of the stack, and its
    /// column has to slide ALONG those faces — the stack's side wall must stay on the part's
    /// surface, or the mesh's boundary is not the part's boundary any more.
    ///
    /// <para>The constraint is met by CONSTRUCTION, not by tolerance: the direction is
    /// projected out of every constraining plane's normal, so <c>p + s*d</c> stays in the
    /// plane through <c>p</c> to round-off at every <c>s</c>, and the side-wall quads are
    /// therefore genuinely part of the non-wall face rather than approximately part of it —
    /// which is what keeps the volume identity at round-off instead of at the projection's
    /// error. What is then CHECKED is how far the projection had to turn the direction: a
    /// large turn means the wall meets its neighbour at a sharp angle, where a layer cannot
    /// both stay on the surface and stand off the wall, and that is refused by name.</para>
    /// </summary>
    private void ApplyJunctionConstraints(Vector3d[] directions, bool refusing)
    {
        for (int v = 0; v < _surface.Length; v++)
        {
            var list = _constraints[v];
            if (list is null || !_isWallVertex[v])
                continue;

            var direction = directions[v];
            foreach (var n in list)
                direction -= n * direction.Dot(n);
            // Two constraints leave a line, and one pass does not reach it when the normals
            // are not orthogonal; a second pass does (it removes the component the first
            // reintroduced), and the residual is measured below either way.
            if (list.Count == 2)
                foreach (var n in list)
                    direction -= n * direction.Dot(n);

            double length = direction.Length;
            if (length <= 0 || (refusing && length < _spec.MinimumConstraintCosine))
                throw new TetMeshException(
                    $"The boundary layer's rim vertex at {_surface[v]} would have to turn its marching " +
                    "direction by more than the allowed " +
                    $"{Math.Acos(Math.Clamp(_spec.MinimumConstraintCosine, -1, 1)) * 180 / Math.PI:F1} " +
                    $"degrees to stay on the {list.Count} non-wall face(s) it touches (the projection " +
                    $"retains {length:F4} of the direction; tag(s) nearby: {NearbyTags(v)}). The wall meets " +
                    "its neighbour at a sharp angle there, and a layer cannot both stay on the surface " +
                    "and stand off the wall. Raise BoundaryLayerSpec.MinimumConstraintCosine only if you " +
                    "have checked what the rim will look like.");

            directions[v] = direction / length;
        }
    }

    private string NearbyTags(int vertex)
    {
        var set = new SortedSet<int>();
        for (int f = 0; f < _faces.Count; f++)
            if (Array.IndexOf(_faces[f], vertex) >= 0)
                set.Add(_tags[f]);
        return set.Count == 0 ? "none" : string.Join(", ", set);
    }

    // ---- the march ----

    private void MarchColumns(Vector3d[] directions)
    {
        int layers = _spec.LayerCount;
        for (int v = 0; v < _surface.Length; v++)
        {
            if (!_isWallVertex[v])
                continue;
            var column = new int[layers + 1];
            column[0] = v;
            var origin = _surface[v];
            var direction = directions[v];
            double height = 0;
            for (int k = 1; k <= layers; k++)
            {
                height += _thicknesses[k - 1];
                column[k] = _nodes.Count;
                _nodes.Add(origin + direction * height);
                _baseOfNode.Add(v);
                _levelOfNode.Add(k);
            }
            _columns[v] = column;
        }
    }

    /// <summary>
    /// A concave corner tighter than the stack folds the marched wall back on itself, which
    /// shows up exactly — per facet, per layer — as a marched triangle whose normal has turned
    /// away from its parent's. Catching it here rather than at element assembly is worth the
    /// separate pass because the message can name the facet, the layer and the height.
    /// </summary>
    private void CheckFolding()
    {
        foreach (int f in _wallFaces)
        {
            var t = _faces[f];
            var baseNormal = (_surface[t[1]] - _surface[t[0]]).Cross(_surface[t[2]] - _surface[t[0]]);
            for (int k = 1; k <= _spec.LayerCount; k++)
            {
                var a = _nodes[_columns[t[0]]![k]];
                var b = _nodes[_columns[t[1]]![k]];
                var c = _nodes[_columns[t[2]]![k]];
                if ((b - a).Cross(c - a).Dot(baseNormal) > 0)
                    continue;
                throw new TetMeshException(
                    $"Boundary layer {k} of {_spec.LayerCount} folds the wall facet {f} (tag {_tags[f]}, " +
                    $"centroid {FaceGeometry(f).Centroid}) back on itself: after marching " +
                    $"{Cumulative(k):G6} the facet has turned inside out. The wall turns faster there " +
                    "than the stack is tall — a convex corner or fillet whose radius is smaller than the " +
                    "stack, or a facet shorter than it. Reduce LayerCount or the layer thicknesses, or " +
                    "round the corner.");
            }
        }
    }

    private double Cumulative(int layers)
    {
        double total = 0;
        for (int i = 0; i < layers; i++)
            total += _thicknesses[i];
        return total;
    }

    /// <summary>
    /// How close the stack came to another WALL, as a ratio of the distance marched — a
    /// reported number and deliberately not a refusal, because it legitimately reads
    /// <c>cos</c> of half a convex corner's angle (0.577 at an ordinary box corner, where the
    /// column marches along the body diagonal) and a threshold tight enough to catch a
    /// collision would refuse those. The refusal is the exact self-intersection test; this is
    /// the number that tells a user how much room is left.
    /// </summary>
    private void MeasureClearance()
    {
        var boxes = new Aabb[_wallFaces.Length];
        for (int i = 0; i < _wallFaces.Length; i++)
        {
            var t = _faces[_wallFaces[i]];
            boxes[i] = Aabb.Empty.Union(_surface[t[0]]).Union(_surface[t[1]]).Union(_surface[t[2]]);
        }
        var bvh = Bvh.Build(boxes);
        double planeSlack = 1e-9 * _extent;
        var hits = new List<int>();

        for (int v = 0; v < _surface.Length; v++)
        {
            var column = _columns[v];
            if (column is null)
                continue;
            for (int k = 1; k <= _spec.LayerCount; k++)
            {
                var point = _nodes[column[k]];
                double marched = Cumulative(k);
                double best = double.PositiveInfinity;
                double radius = marched;
                for (int attempt = 0; attempt < 40 && double.IsPositiveInfinity(best); attempt++)
                {
                    hits.Clear();
                    var extent = new Vector3d(radius, radius, radius);
                    bvh.Query(new Aabb(point - extent, point + extent), hits);
                    foreach (int i in hits)
                    {
                        var t = _faces[_wallFaces[i]];
                        if (t[0] == v || t[1] == v || t[2] == v)
                            continue;   // the wall this column deliberately left
                        var (_, normal, _) = FaceGeometry(_wallFaces[i]);
                        if (Math.Abs((point - _surface[t[0]]).Dot(normal)) <= planeSlack)
                            continue;   // a face this column is sliding along, not one it hit
                        double d = (Distance3d.ClosestPointOnTriangle(
                            point, _surface[t[0]], _surface[t[1]], _surface[t[2]]) - point).Length;
                        if (d < best)
                            best = d;
                    }
                    radius *= 2;
                }
                if (double.IsPositiveInfinity(best) || marched <= 0)
                    continue;
                _minClearance = Math.Min(_minClearance, best / marched);
            }
        }
    }

    // ---- the surface the isotropic mesher gets ----

    /// <summary>
    /// The closed surface bounding what the stack did not fill, per body: the OFFSET wall (the
    /// stack's innermost level) plus the non-wall faces, with any planar patch whose rim the
    /// stack ate rebuilt around the trimmed rim.
    ///
    /// <para>Re-triangulating a whole affected patch rather than only the strip the stack took
    /// is deliberate and costs nothing geometrically — a patch is planar, so the surface as a
    /// POINT SET is unchanged, which is the property boundary recovery actually tests. What it
    /// does cost is the patch's interior node placement, which the isotropic fill then sizes
    /// itself.</para>
    /// </summary>
    private List<CoreSurface> BuildCoreSurfaces()
    {
        int bodies = 0;
        foreach (int b in _faceBody)
            bodies = Math.Max(bodies, b + 1);

        // Which wall patch each wall face belongs to, indexed by GLOBAL face index.
        var patchOfFace = new int[_faces.Count];
        Array.Fill(patchOfFace, -1);
        for (int p = 0; p < _wallPatches.Count; p++)
            foreach (int local in _wallPatches[p].Triangles)
                patchOfFace[_wallFaces[local]] = p;

        var result = new List<CoreSurface>();
        for (int body = 0; body < bodies; body++)
        {
            var triangles = new List<int[]>();
            var triangleTags = new List<int>();

            int n = _spec.LayerCount;
            foreach (int f in _wallFaces)
            {
                if (_faceBody[f] != body)
                    continue;
                var t = _faces[f];
                triangles.Add([_columns[t[0]]![n], _columns[t[1]]![n], _columns[t[2]]![n]]);
                triangleTags.Add(ReservedBase + patchOfFace[f]);
            }

            AppendNonWallSurface(body, triangles, triangleTags);
            if (triangles.Count == 0)
                continue;

            var (positions, indices) = Compact(triangles);
            HalfEdgeMesh surface;
            try
            {
                surface = HalfEdgeMesh.Build(positions, indices);
            }
            catch (ArgumentException ex)
            {
                throw new TetMeshException(
                    "The surface left over after the boundary layer is not a valid closed manifold. The " +
                    "stack's inner face and the trimmed non-wall faces do not join up, which happens when " +
                    "the wall selection's rim runs across a curved face or doubles back on itself. " +
                    $"(Underlying: {ex.Message})", ex);
            }
            if (!surface.IsClosed)
                throw new TetMeshException(
                    "The surface left over after the boundary layer is OPEN. The stack's inner face and " +
                    "the trimmed non-wall faces leave a gap, which means the wall selection's rim does " +
                    "not close.");

            result.Add(new CoreSurface(surface, [.. triangleTags], body));
        }

        if (result.Count == 0)
            throw new TetMeshException(
                "The boundary layer consumed the whole model: no volume is left for the isotropic fill. " +
                "Reduce LayerCount or the layer thicknesses.");
        return result;
    }

    /// <summary>
    /// The non-wall faces of one body, with rim-touching planar patches rebuilt. A patch with
    /// no rim vertex is copied verbatim, which keeps an untouched face's own triangulation —
    /// and therefore its node placement — exactly as the caller supplied it.
    /// </summary>
    private void AppendNonWallSurface(int body, List<int[]> triangles, List<int> triangleTags)
    {
        var patchFaces = new List<int[]>();
        var patchTags = new List<int>();
        for (int f = 0; f < _faces.Count; f++)
        {
            if (_isWallFace[f] || _faceBody[f] != body)
                continue;
            patchFaces.Add(_faces[f]);
            patchTags.Add(_tags[f]);
        }
        if (patchFaces.Count == 0)
            return;

        var patches = SurfacePatches.Build(_surface, patchFaces, patchTags, PlaneTolerance, _extent);
        foreach (var patch in patches)
        {
            bool touched = false;
            foreach (int local in patch.Triangles)
                foreach (int v in patchFaces[local])
                    if (_columns[v] is not null)
                        touched = true;

            if (!touched)
            {
                foreach (int local in patch.Triangles)
                {
                    triangles.Add(patchFaces[local]);
                    triangleTags.Add(patchTags[local]);
                }
                continue;
            }

            Retriangulate(patch, patchFaces, triangles, triangleTags);
            _retriangulatedPatches++;
        }
    }

    /// <summary>
    /// Rebuilds one planar patch around its trimmed rim. The patch's boundary loops are walked
    /// from its own edge use counts, every rim vertex is replaced by the deepest node of its
    /// column (which lies exactly in this patch's plane, by construction), and the result is
    /// ear-clipped in the patch's own 2D frame.
    /// </summary>
    private void Retriangulate(
        SurfacePatch patch, List<int[]> patchFaces, List<int[]> triangles, List<int> triangleTags)
    {
        var used = new Dictionary<(int, int), int>();
        var directed = new List<(int, int)>();
        foreach (int local in patch.Triangles)
        {
            var t = patchFaces[local];
            for (int e = 0; e < 3; e++)
            {
                int a = t[e], b = t[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                used[key] = used.GetValueOrDefault(key) + 1;
                directed.Add((a, b));
            }
        }

        // Boundary directed edges are those whose undirected key is used once. Walking them in
        // their own direction reproduces the patch's outward winding, so the loops come out
        // oriented with no second decision to get wrong.
        var next = new Dictionary<int, int>();
        foreach (var (a, b) in directed)
        {
            var key = a < b ? (a, b) : (b, a);
            if (used[key] != 1)
                continue;
            if (!next.TryAdd(a, b))
                throw new TetMeshException(
                    $"A non-wall planar patch (tag {patch.Tag}) has a vertex where its boundary branches, " +
                    "so it cannot be re-triangulated around the boundary layer's rim. Split the face, or " +
                    "extend the wall selection over it.");
        }

        var loops = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (int start in next.Keys)
        {
            if (!seen.Add(start))
                continue;
            var loop = new List<int> { start };
            int current = next[start];
            while (current != start)
            {
                if (!seen.Add(current) || !next.TryGetValue(current, out int following))
                    throw new TetMeshException(
                        $"A non-wall planar patch (tag {patch.Tag}) has an open boundary, so it cannot be " +
                        "re-triangulated around the boundary layer's rim.");
                loop.Add(current);
                current = following;
            }
            loops.Add(loop);
        }
        if (loops.Count == 0)
            throw new TetMeshException($"A non-wall planar patch (tag {patch.Tag}) has no boundary loop.");

        // Replace every rim vertex by the deepest node of its column.
        int deepest = _spec.LayerCount;
        var replaced = new List<List<int>>(loops.Count);
        foreach (var loop in loops)
        {
            var mapped = new List<int>(loop.Count);
            foreach (int v in loop)
                mapped.Add(_columns[v] is { } column ? column[deepest] : v);
            replaced.Add(mapped);
        }

        // Project into the patch's own frame. The rim nodes lie in this plane by construction,
        // so the local z is round-off and dropping it invents nothing.
        var frame = patch.Frame;
        var flattened = new List<List<Vector2d>>(replaced.Count);
        foreach (var loop in replaced)
        {
            var flat = new List<Vector2d>(loop.Count);
            foreach (int v in loop)
            {
                var local = frame.ToLocal(_nodes[v]);
                flat.Add(new Vector2d(local.X, local.Y));
            }
            flattened.Add(flat);
        }

        // The outer loop is the one with the largest absolute area; the rest are holes.
        int outer = 0;
        double bestArea = 0;
        for (int i = 0; i < flattened.Count; i++)
        {
            double area = Math.Abs(PolygonTriangulator.SignedArea(flattened[i]));
            if (area > bestArea)
            {
                bestArea = area;
                outer = i;
            }
        }

        var outerLoop = flattened[outer];
        var flatIndices = new List<int>(replaced[outer]);
        var holes = new List<IReadOnlyList<Vector2d>>();
        for (int i = 0; i < flattened.Count; i++)
        {
            if (i == outer)
                continue;
            holes.Add(flattened[i]);
            flatIndices.AddRange(replaced[i]);
        }

        // The trimmed outline must still wind the way the original did. If it has turned
        // inside out, the stacks growing from the faces AROUND this one have met in the middle
        // of it and the face is gone — the commonest way to ask for a layer that does not fit,
        // and the one a self-intersection test cannot see, because two flat sheets that have
        // swapped places are parallel and never cross.
        double before = PolygonTriangulator.SignedArea(
            [.. loops[outer].Select(v => { var l = frame.ToLocal(_surface[v]); return new Vector2d(l.X, l.Y); })]);
        double after = PolygonTriangulator.SignedArea(outerLoop);
        if (before * after <= 0)
            throw new TetMeshException(
                $"The boundary layer has eaten through the face tagged {patch.Tag}: after marching " +
                $"{_spec.TotalThickness:G6} over {_spec.LayerCount} layer(s), its trimmed outline has " +
                $"turned inside out (signed area {before:G4} became {after:G4}). The stacks growing from " +
                "the faces around it have met in the middle. Reduce LayerCount or the layer thicknesses.");

        var earcut = holes.Count == 0
            ? PolygonTriangulator.Triangulate(outerLoop)
            : PolygonTriangulator.TriangulateWithHoles(outerLoop, holes);
        if (earcut.Count == 0)
            throw new TetMeshException(
                $"A non-wall planar patch (tag {patch.Tag}) could not be re-triangulated around the " +
                "boundary layer's rim: the trimmed outline encloses no area, so the stack has eaten the " +
                "whole face. Reduce LayerCount or the layer thicknesses.");

        // The frame's +z is the patch normal, so a counter-clockwise triangle in the frame is
        // wound outward; earcut's own orientation follows the outer loop's signed area.
        bool flip = PolygonTriangulator.SignedArea(outerLoop) < 0;
        foreach (var (a, b, c) in earcut)
        {
            triangles.Add(flip
                ? [flatIndices[a], flatIndices[c], flatIndices[b]]
                : [flatIndices[a], flatIndices[b], flatIndices[c]]);
            triangleTags.Add(patch.Tag);
        }
    }

    /// <summary>Compacts a triangle list over the node array into its own vertex numbering.</summary>
    private (List<Vector3d> Positions, List<int[]> Faces) Compact(List<int[]> triangles)
    {
        var map = new Dictionary<int, int>();
        var positions = new List<Vector3d>();
        var faces = new List<int[]>(triangles.Count);

        int Map(int v)
        {
            if (map.TryGetValue(v, out int mapped))
                return mapped;
            mapped = positions.Count;
            map[v] = mapped;
            positions.Add(_nodes[v]);
            return mapped;
        }

        foreach (var t in triangles)
            faces.Add([Map(t[0]), Map(t[1]), Map(t[2])]);
        return (positions, faces);
    }

    // ==================================================================
    // Pass 2: read the interface back off the fill, and weld.
    // ==================================================================

    /// <summary>Vertex indices of a tetrahedron's four faces, wound OUTWARD.</summary>
    private static readonly int[][] FaceTable =
    [
        [1, 2, 3],
        [0, 3, 2],
        [0, 1, 3],
        [0, 2, 1],
    ];

    /// <summary>
    /// Builds the stack's elements on the interface triangulation the fill chose, then welds
    /// the two meshes into one.
    ///
    /// <para><b>The weld is by exact position, and the conformity check falls out of it.</b>
    /// The core surface was built FROM these nodes and is only ever copied, never recomputed,
    /// so a fill vertex sitting on the interface carries bit-identical coordinates and a
    /// dictionary on the bits finds it. Each interface triangle is then used by two elements,
    /// one from each side, and vanishes from the combined mesh's boundary — so <b>"every
    /// boundary face has a known tag" IS the statement that the two meshes conform</b>. There
    /// is no separate check to keep in step with the welding, and an interface the fill
    /// refined behind the stack's back shows up immediately as a boundary face nobody
    /// claims.</para>
    /// </summary>
    internal TetMesh Complete(TetMesh core, out BoundaryLayerReport report)
    {
        var nodes = _nodes;
        var byPosition = new Dictionary<Vector3d, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
            byPosition.TryAdd(nodes[i], i);

        var coreToGlobal = new int[core.VertexCount];
        for (int v = 0; v < core.VertexCount; v++)
        {
            var p = core.Position(v);
            if (byPosition.TryGetValue(p, out int existing))
            {
                coreToGlobal[v] = existing;
                continue;
            }
            coreToGlobal[v] = nodes.Count;
            byPosition[p] = nodes.Count;
            nodes.Add(p);
            _baseOfNode.Add(-1);
            _levelOfNode.Add(-1);
        }

        EmitStack(core, coreToGlobal);
        EmitSideWallTags();

        var tets = new List<int>(_tets);
        var regions = new List<int>(_regions);
        for (int t = 0; t < core.TetCount; t++)
        {
            var tet = core.GetTet(t);
            tets.Add(coreToGlobal[tet.A]);
            tets.Add(coreToGlobal[tet.B]);
            tets.Add(coreToGlobal[tet.C]);
            tets.Add(coreToGlobal[tet.D]);
            regions.Add(core.RegionOf(t));
        }

        // Tags for the faces the fill owns. The interface's reserved tags are dropped
        // deliberately: those facets are internal now and must NOT survive as boundary.
        var tagged = new Dictionary<(int, int, int), int>(_exposedTags);
        foreach (var facet in core.BoundaryFacets)
        {
            if (facet.SourceTriangle >= ReservedBase)
                continue;
            tagged[SortedKey(coreToGlobal[facet.V0], coreToGlobal[facet.V1], coreToGlobal[facet.V2])]
                = facet.SourceTriangle;
        }

        var mesh = Assemble(nodes, tets, regions, tagged);

        double stackVolume = 0;
        for (int t = 0; t < _regions.Count; t++)
        {
            stackVolume += TetMesh.SignedVolume(
                nodes[_tets[4 * t]], nodes[_tets[4 * t + 1]],
                nodes[_tets[4 * t + 2]], nodes[_tets[4 * t + 3]]);
        }

        MeasureMarch(out double firstMeasured, out double worstRatio);
        report = new BoundaryLayerReport(
            WallTriangles: _wallFaces.Length,
            Layers: _spec.LayerCount,
            ElementCount: _regions.Count,
            Nodes: CountMarchedNodes(),
            JunctionNodes: _junctionNodes,
            RetriangulatedPatches: _retriangulatedPatches,
            TotalThickness: _spec.TotalThickness,
            FirstLayerThickness: firstMeasured,
            MeasuredGrowthRatio: worstRatio,
            StackVolume: stackVolume,
            MinMarchClearance: double.IsPositiveInfinity(_minClearance) ? 1.0 : _minClearance);
        return mesh;
    }

    private int CountMarchedNodes()
    {
        int count = 0;
        for (int v = 0; v < _surface.Length; v++)
            if (_columns[v] is not null)
                count += _spec.LayerCount;
        return count;
    }

    /// <summary>
    /// The stack's elements, built on the interface triangulation the fill produced: every
    /// boundary facet of the fill carrying a reserved tag is one prism column, mapped back to
    /// the wall vertices its nodes marched from.
    /// </summary>
    private void EmitStack(TetMesh core, int[] coreToGlobal)
    {
        int layers = _spec.LayerCount;
        int found = 0;
        var baseTriple = new int[3];
        var corner = new int[3];
        foreach (var facet in core.BoundaryFacets)
        {
            if (facet.SourceTriangle < ReservedBase)
                continue;
            found++;
            int patch = facet.SourceTriangle - ReservedBase;

            corner[0] = facet.V0;
            corner[1] = facet.V1;
            corner[2] = facet.V2;
            for (int i = 0; i < 3; i++)
            {
                int node = coreToGlobal[corner[i]];
                int baseVertex = _baseOfNode[node];
                if (baseVertex < 0 || _levelOfNode[node] != layers
                    || _vertexWallPatches[baseVertex] is not { } patches || !patches.Contains(patch))
                {
                    throw new TetMeshException(
                        "The isotropic fill changed the boundary layer's interface: its facet at " +
                        $"{core.Position(corner[i])} uses a vertex the stack never marched there, so the " +
                        "two cannot be welded. The fill inserted a point on a surface the stack had " +
                        "already committed to — pre-refine the wall surface so the layer is built at the " +
                        "size you want, rather than asking the fill to refine it afterwards.");
                }
                baseTriple[i] = baseVertex;
            }

            // The facet is wound outward from the fill, which at the interface points into the
            // stack - the same sense the original wall carries. So this triple is the wall's
            // own winding and needs no fix.
            int body = core.RegionOf(facet.Tet);
            int tag = _wallPatches[patch].Tag;
            _exposedTags[SortedKey(baseTriple[0], baseTriple[1], baseTriple[2])] = tag;

            for (int k = 1; k <= layers; k++)
            {
                var c0 = _columns[baseTriple[0]]!;
                var c1 = _columns[baseTriple[1]]!;
                var c2 = _columns[baseTriple[2]]!;
                EmitPrism(baseTriple[0], baseTriple[1], baseTriple[2],
                          c0[k], c1[k], c2[k], c0[k - 1], c1[k - 1], c2[k - 1], body, tag);
            }
        }

        if (found == 0)
            throw new TetMeshException(
                "The isotropic fill returned no facets on the boundary layer's interface, so the stack " +
                "has nothing to stand on. The offset wall was classified away, which means the volume " +
                "left over is not the shape the layer expected.");
    }

    /// <summary>
    /// One prism, as three tetrahedra. <paramref name="b0"/>..<paramref name="b2"/> are the
    /// INNER (deeper) triangle and <paramref name="u0"/>..<paramref name="u2"/> the outer one,
    /// both in the wall's winding — which is what makes every tetrahedron here positively
    /// oriented without a sign convention to remember: the inner triangle's outward normal
    /// points at the outer one.
    /// </summary>
    private void EmitPrism(
        int base0, int base1, int base2,
        int b0, int b1, int b2, int u0, int u1, int u2,
        int body, int tag)
    {
        // Rotate so the smallest INPUT-SURFACE vertex index leads. That index is what two
        // prisms sharing a quad both see, so it is what makes their diagonals agree.
        while (base0 > base1 || base0 > base2)
        {
            (base0, base1, base2) = (base1, base2, base0);
            (b0, b1, b2) = (b1, b2, b0);
            (u0, u1, u2) = (u1, u2, u0);
        }

        if (base1 < base2)
        {
            // Diagonals a-b', b-c', a-c'.
            Add(b0, b1, b2, u2, body, tag);
            Add(b0, b1, u2, u1, body, tag);
            Add(b0, u1, u2, u0, body, tag);
        }
        else
        {
            // Diagonals a-b', c-b', a-c'.
            Add(b0, b1, b2, u1, body, tag);
            Add(b0, u1, b2, u2, body, tag);
            Add(b0, u1, u2, u0, body, tag);
        }
    }

    private void Add(int a, int b, int c, int d, int body, int tag)
    {
        if (Predicates3d.SignedVolume6Sign(_nodes[a], _nodes[b], _nodes[c], _nodes[d]) <= 0)
            throw new TetMeshException(
                $"A boundary-layer element on the wall tagged {tag} came out flat or inverted (corners " +
                $"{_nodes[a]}, {_nodes[b]}, {_nodes[c]}, {_nodes[d]}). The march collapsed the prism " +
                "there: the wall is concave on a radius smaller than the stack is tall, or two of its " +
                "facets are nearly coplanar with opposite normals. Reduce LayerCount or the layer " +
                "thicknesses.");
        _tets.Add(a);
        _tets.Add(b);
        _tets.Add(c);
        _tets.Add(d);
        _regions.Add(body);
    }

    /// <summary>
    /// The stack's side walls: where a wall edge is shared with a NON-wall face, the two
    /// columns sweep a quadrilateral per layer, and that quadrilateral lies IN the non-wall
    /// face's plane — exactly what the junction constraint bought. It carries the non-wall
    /// face's tag, because it is part of that surface.
    ///
    /// <para>The quad's diagonal is not a free choice here either: it must be the one the
    /// prism decomposition already committed to, or these triangles are faces of no
    /// tetrahedron and the tag would never be found. Reading <see cref="EmitPrism"/>'s rule
    /// back out, every quad's diagonal joins the INNER node of the lower-indexed base vertex
    /// to the OUTER node of the higher-indexed one, whatever the third vertex is — which is
    /// also why the side walls do not depend on the interface triangulation.</para>
    /// </summary>
    private void EmitSideWallTags()
    {
        var edgeFaces = new Dictionary<(int, int), (int First, int Second)>();
        for (int f = 0; f < _faces.Count; f++)
        {
            var t = _faces[f];
            for (int e = 0; e < 3; e++)
            {
                int a = t[e], b = t[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edgeFaces[key] = edgeFaces.TryGetValue(key, out var pair) ? (pair.First, f) : (f, -1);
            }
        }

        foreach (int f in _wallFaces)
        {
            var t = _faces[f];
            for (int e = 0; e < 3; e++)
            {
                int a = t[e], b = t[(e + 1) % 3];
                var pair = edgeFaces[a < b ? (a, b) : (b, a)];
                int partner = pair.First == f ? pair.Second : pair.First;
                if (partner < 0 || _isWallFace[partner])
                    continue;

                int tag = _tags[partner];
                var low = _columns[Math.Min(a, b)]!;
                var high = _columns[Math.Max(a, b)]!;
                for (int k = 1; k <= _spec.LayerCount; k++)
                {
                    // Diagonal: inner(low) - outer(high).
                    _exposedTags[SortedKey(low[k], high[k], high[k - 1])] = tag;
                    _exposedTags[SortedKey(low[k], high[k - 1], low[k - 1])] = tag;
                }
            }
        }
    }

    private TetMesh Assemble(
        List<Vector3d> nodes, List<int> tets, List<int> regions,
        Dictionary<(int, int, int), int> tagged)
    {
        var usage = new Dictionary<(int, int, int), (int Count, int Tet, int Face)>();
        for (int t = 0; t < regions.Count; t++)
        {
            for (int f = 0; f < 4; f++)
            {
                var key = SortedKey(
                    tets[4 * t + FaceTable[f][0]], tets[4 * t + FaceTable[f][1]], tets[4 * t + FaceTable[f][2]]);
                usage[key] = usage.TryGetValue(key, out var seen)
                    ? (seen.Count + 1, seen.Tet, seen.Face)
                    : (1, t, f);
            }
        }

        var facets = new List<TetFacet>();
        var orphans = new List<(int, int, int)>();
        foreach (var (key, (count, tet, face)) in usage)
        {
            if (count != 1)
                continue;
            if (!tagged.TryGetValue(key, out int tag))
            {
                orphans.Add(key);
                continue;
            }
            facets.Add(new TetFacet(
                tets[4 * tet + FaceTable[face][0]],
                tets[4 * tet + FaceTable[face][1]],
                tets[4 * tet + FaceTable[face][2]],
                tet, tag));
        }

        if (orphans.Count > 0)
        {
            var (a, b, c) = orphans[0];
            throw new TetMeshException(
                $"{orphans.Count} face(s) of the combined mesh lie on its boundary but belong to neither " +
                "the boundary layer's own skin nor the isotropic fill's, so the two do not conform: the " +
                $"first spans {nodes[a]} / {nodes[b]} / {nodes[c]}. This means the stack and the fill " +
                "disagree about the interface between them.");
        }

        // Drop nodes nothing uses. Re-triangulating an affected planar patch legitimately
        // abandons its old interior vertices, and a node with no element carries a degree of
        // freedom with no stiffness, which is a singular row rather than a tidiness problem.
        var remap = new int[nodes.Count];
        Array.Fill(remap, -1);
        var kept = new List<Vector3d>();
        for (int i = 0; i < tets.Count; i++)
        {
            int v = tets[i];
            if (remap[v] < 0)
            {
                remap[v] = kept.Count;
                kept.Add(nodes[v]);
            }
            tets[i] = remap[v];
        }

        var final = new TetFacet[facets.Count];
        for (int i = 0; i < facets.Count; i++)
        {
            var f = facets[i];
            final[i] = new TetFacet(remap[f.V0], remap[f.V1], remap[f.V2], f.Tet, f.SourceTriangle);
        }
        return new TetMesh([.. kept], [.. tets], [.. regions], final);
    }

    private void MeasureMarch(out double firstMeasured, out double worstRatio)
    {
        firstMeasured = 0;
        worstRatio = _spec.GrowthRatio;
        double worstDeviation = 0;
        for (int v = 0; v < _surface.Length; v++)
        {
            var column = _columns[v];
            if (column is null)
                continue;
            double previous = 0;
            for (int k = 1; k <= _spec.LayerCount; k++)
            {
                double measured = (_nodes[column[k]] - _nodes[column[k - 1]]).Length;
                if (k == 1 && firstMeasured == 0)
                    firstMeasured = measured;
                if (k > 1 && previous > 0)
                {
                    double ratio = measured / previous;
                    double deviation = Math.Abs(ratio - _spec.GrowthRatio);
                    if (deviation > worstDeviation)
                    {
                        worstDeviation = deviation;
                        worstRatio = ratio;
                    }
                }
                previous = measured;
            }
        }
    }

    internal static (int, int, int) SortedKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
