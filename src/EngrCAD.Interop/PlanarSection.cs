using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// Planar cross-sections of a solid, as 2D <see cref="Region2d"/>s in the cutting plane's
/// own coordinates — OpenSCAD's <c>projection(cut = true)</c>, the drawing-view section, and
/// the input a hidden-line pass would consume.
///
/// <para><b>Two routes, one answer.</b> <see cref="OfMesh"/> reuses
/// <see cref="MeshPlaneCut"/>'s ordered boundary loops and hands them to
/// <see cref="Region2d.FromLoops"/>, which detects the nesting itself (a bore inside a plate
/// becomes a hole without anyone declaring it). <see cref="OfSolid"/> works from the exact
/// B-Rep: <see cref="SurfaceIntersection"/> per face, trimmed to the face, then chained into
/// loops. The B-Rep route's fidelity is set by <c>chordTolerance</c> alone rather than by
/// whatever tessellation the display happens to use, so a bore rim comes out as smooth as
/// asked for.</para>
///
/// <para><b>Why loop assembly is exact.</b> A section curve leaves a face exactly where the
/// plane crosses one of the face's EDGES, and that edge is shared by the neighbouring face.
/// So the crossings are solved once per edge, on the edge's own exact curve, and both faces
/// use the SAME point — the section polygon closes by construction rather than by welding
/// two independently computed endpoints (which would be the 1e-7 seam tier at best). Those
/// crossings also partition each face's intersection curve, and each piece is kept or
/// dropped by one <see cref="FaceGeometry.Contains"/> probe at its MIDPOINT, never at an
/// endpoint sitting on the trim boundary.</para>
///
/// <para><b>The plane must not be flush with a planar face</b>: a coplanar face's section is
/// two-dimensional, not a curve, and the answer would depend on which way the tie broke.
/// <see cref="OfSolid"/> says so rather than returning something plausible. The mesh route
/// has the same restriction implicitly (<see cref="MeshPlaneCut"/> keeps on-plane vertices
/// where they are).</para>
/// </summary>
public static class PlanarSection
{
    /// <summary>Default flattening of curved section geometry: 1 µm sagitta, matching
    /// <c>Sketch.DefaultChordTolerance</c> and <c>Region2dOffset.DefaultArcTolerance</c>.</summary>
    public const double DefaultChordTolerance = 1e-3;

    /// <summary>Minimum samples per section curve — enough that a probe midpoint cannot
    /// straddle a whole feature even when the chord tolerance asks for very few.</summary>
    private const int MinimumCurveSamples = 8;

    /// <summary>
    /// The cross-section of a closed mesh through <paramref name="plane"/>, in the plane's
    /// own 2D coordinates (the frame's X/Y; the frame's Z is the cutting normal).
    /// Nesting is re-derived, so cavities become holes. A plane that misses the mesh — or
    /// only touches it — returns an empty list rather than throwing.
    /// </summary>
    public static IReadOnlyList<Region2d> OfMesh(HalfEdgeMesh mesh, in Frame3d plane)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var origin = plane.Origin;
        var normal = plane.Z;
        if (!StraddlesPlane(mesh, origin, normal))
            return [];

        var cut = MeshPlaneCut.Cut(mesh, origin, normal, cap: false);
        var loops = new List<IReadOnlyList<Vector2d>>(cut.CutLoops.Count);
        foreach (var loop in cut.CutLoops)
        {
            var flat = new Vector2d[loop.Count];
            for (int i = 0; i < loop.Count; i++)
            {
                var local = plane.ToLocal(loop[i]);
                flat[i] = new Vector2d(local.X, local.Y);
            }
            loops.Add(flat);
        }
        return Region2d.FromLoops(loops);
    }

    /// <summary>
    /// The cross-section of a B-Rep solid through <paramref name="plane"/>, in the plane's
    /// own 2D coordinates. Curved section geometry is flattened to
    /// <paramref name="chordTolerance"/> (regions are polygonal); straight sections are exact.
    /// </summary>
    /// <exception cref="NotSupportedException">The plane is flush with a planar face.</exception>
    public static IReadOnlyList<Region2d> OfSolid(
        BrepSolid solid, in Frame3d plane, double chordTolerance = DefaultChordTolerance)
    {
        ArgumentNullException.ThrowIfNull(solid);
        if (!(chordTolerance > 0))
            throw new ArgumentOutOfRangeException(nameof(chordTolerance), "The chord tolerance must be positive.");

        var origin = plane.Origin;
        var normal = plane.Z;
        var planeSurface = new PlaneSurface(plane);
        double weld = Tolerance.Default.Linear;

        // 0. Degenerate placements, diagnosed before any work — flush faces first, since a
        //    flush face is the more informative complaint than the four in-plane edges it
        //    inevitably brings with it.
        foreach (var face in solid.Faces)
        {
            if (BoundsStraddle(face.Bounds(), origin, normal))
                RejectFlushFace(face, origin, normal);
        }

        // 1. Where the plane crosses each EDGE. Solved once, on the edge's own exact curve,
        //    so both faces using that edge get the identical point.
        var nodes = new List<Vector3d>();
        foreach (var edge in solid.Edges)
        {
            RejectInPlaneEdge(edge, origin, normal, weld);
            CollectPlaneCrossings(edge, origin, normal, weld, nodes);
        }

        var loops = new List<IReadOnlyList<Vector2d>>();
        var openRuns = new List<SectionRun>();

        // 2. Per face: intersect with the plane, split at this face's nodes, keep the pieces
        //    whose midpoint is inside the face.
        foreach (var face in solid.Faces)
        {
            var bounds = face.Bounds();
            if (!BoundsStraddle(bounds, origin, normal))
                continue;

            var grown = bounds.Expanded(weld);
            var faceNodes = new List<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (grown.Contains(nodes[i]))
                    faceNodes.Add(i);
            }

            foreach (var curve in SurfaceIntersection.Intersect(planeSurface, face.Surface, grown))
                CollectFaceRuns(curve, face, faceNodes, nodes, chordTolerance, weld, loops, openRuns, plane);
        }

        // 3. Chain the open runs end to end. Endpoints are shared node positions, so the
        //    chaining key is exact equality of the node index rather than a distance weld.
        ChainRuns(openRuns, plane, loops);
        return Region2d.FromLoops(loops);
    }

    /// <summary>
    /// The SILHOUETTE of a mesh viewed along the plane's normal — OpenSCAD's
    /// <c>projection(cut = false)</c>: the outline the body would cast, as 2D regions in the
    /// plane's coordinates. Internal cavities that do not reach the outline disappear (they
    /// are covered by the material in front of them); a through-hole survives as a hole.
    ///
    /// <para><b>Approach and cost.</b> Every triangle's projection is a region and the
    /// silhouette is their union — but unioning thousands of triangles naively is the
    /// expensive way to do anything in 2D, so three things shape the work:</para>
    /// <list type="number">
    /// <item><b>Back-facing triangles are dropped first</b>, halving the input. This is
    /// EXACT for a closed mesh and only for a closed mesh: a ray along the normal through
    /// any point of the shadow leaves the solid through a front-facing triangle, so the
    /// front-facing projections already cover the whole outline. An open mesh keeps every
    /// triangle, because that argument does not hold.</item>
    /// <item><b>Triangles are Morton-sorted by projected centroid</b>, so the fold merges
    /// neighbours first and intermediate boundaries stay simple. Sorting is what makes the
    /// tree pay: merging triangle 1 with triangle 900 produces two disjoint regions and no
    /// cancellation at all.</item>
    /// <item><b>The fold is <see cref="Region2dBoolean.UnionAll"/>'s balanced tree</b>, and
    /// each merge discards its children's interior edges before climbing — which is why the
    /// cost grows with the OUTLINE's complexity rather than the triangle count.</item>
    /// </list>
    ///
    /// <para><b>Projected coordinates are quantized</b> to <see cref="SilhouetteGrid"/> times
    /// the outline's extent before the union — see that constant. Without it the answer
    /// depends on the merge order, and the tracer can dead-end outright.</para>
    ///
    /// <para>Being honest about the cost: the union dominates, and its shape matters far more
    /// than the triangle count. Measured on a torus tessellated at 64 segments (3072
    /// front-facing faces): Morton-sorted balanced tree <b>67 ms</b>, unsorted balanced tree
    /// <b>2.4 s</b>, linear accumulate <b>259 s</b>. A 128-segment sphere (12k front-facing
    /// faces) takes ~240 ms. Tessellate coarsely when an approximate outline will do — the
    /// union is exact for whatever mesh it is given, so mesh fidelity is the knob.</para>
    /// </summary>
    public static IReadOnlyList<Region2d> SilhouetteOfMesh(HalfEdgeMesh mesh, in Frame3d plane)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var normal = plane.Z;
        bool closed = mesh.IsClosed;

        // Pass 1: project, cull, and measure the extent (which sets the quantization step).
        var projected = new List<List<Vector2d>>(mesh.FaceCount);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            var face = mesh.GetFace(f);
            // Drop back faces on a closed mesh (see the remarks) — and drop nothing on an
            // open one, where the covering argument fails.
            if (closed && face.NormalRaw.Dot(normal) <= 0)
                continue;

            var loop = new List<Vector2d>(3);
            foreach (var vertex in face.Vertices())
            {
                var flat = Flat(plane, vertex.Position);
                loop.Add(flat);
                minX = Math.Min(minX, flat.X);
                minY = Math.Min(minY, flat.Y);
                maxX = Math.Max(maxX, flat.X);
                maxY = Math.Max(maxY, flat.Y);
            }
            if (loop.Count >= 3)
                projected.Add(loop);
        }
        if (projected.Count == 0)
            return [];

        double grid = Math.Max(maxX - minX, maxY - minY) * SilhouetteGrid;

        // Pass 2: quantize, drop the edge-on faces, and note the centroids for the sort.
        var faces = new List<(Vector2d[] Loop, Vector2d Centre)>(projected.Count);
        var bounds = Aabb.Empty;
        foreach (var loop in projected)
        {
            var snapped = new Vector2d[loop.Count];
            var centre = Vector2d.Zero;
            for (int i = 0; i < loop.Count; i++)
            {
                snapped[i] = Quantize(loop[i], grid);
                centre += snapped[i];
            }
            // Exact-zero test: a face projecting edge-on covers no area at all.
            if (Region2d.SignedArea(snapped) == 0)
                continue;
            centre /= snapped.Length;
            faces.Add((snapped, centre));
            bounds = bounds.Union((centre.X, centre.Y, 0));
        }
        if (faces.Count == 0)
            return [];

        var sorted = MortonOrder(faces, bounds);
        var regions = new List<Region2d>(sorted.Count);
        foreach (int index in sorted)
            regions.Add(new Region2d(faces[index].Loop));
        return Region2dBoolean.UnionAll(regions);
    }

    /// <summary>
    /// Quantization step for silhouette coordinates, RELATIVE to the outline's extent
    /// (the scale-free tier — never an absolute weld tolerance).
    ///
    /// <para><b>Why it is needed.</b> Two mesh vertices that lie on the same feature line —
    /// a torus's latitude ring, a cylinder's rim — are only equal to within ULPS once
    /// projected, because each was evaluated independently. Two edges that should be
    /// collinear then sit ~2e-16 apart, which is far too small for the arrangement's
    /// crossing logic to see as a T-junction and far too large to ignore: it leaves a sliver
    /// cell one ULP thick, whose interior sample rounds back onto its own boundary.
    /// Downstream, the union's answer starts to depend on the merge order (measured: a
    /// 16-segment torus viewed side-on came out 60.42 unsorted and 59.33 Morton-sorted, the
    /// truth being 60.42), and a 64-segment one threw "boundary tracing hit a dead end"
    /// outright. Snapping to a grid ~4500 ULPs wide collapses those pairs to identical
    /// doubles, so the arrangement dedupes them as coincident edges and no sliver is ever
    /// built. With it, every merge order agrees and the areas converge cleanly on the
    /// analytic value.</para>
    ///
    /// <para>1e-12 of the extent is nine orders below the chord tolerance a polygonal region
    /// carries anyway, so it cannot move a boundary a caller could notice.</para>
    /// </summary>
    public const double SilhouetteGrid = 1e-12;

    private static Vector2d Quantize(in Vector2d point, double grid) =>
        grid > 0                                   // exact-zero guard: a degenerate extent
            ? new Vector2d(Math.Round(point.X / grid) * grid, Math.Round(point.Y / grid) * grid)
            : point;

    private static Vector2d Flat(in Frame3d plane, in Vector3d point)
    {
        var local = plane.ToLocal(point);
        return new Vector2d(local.X, local.Y);
    }

    /// <summary>Indices in Morton (Z-curve) order of the projected centroids, quantized to
    /// 16 bits per axis over the centroid bounds — spatial locality so the balanced union
    /// merges neighbours first.</summary>
    private static List<int> MortonOrder(
        List<(Vector2d[] Loop, Vector2d Centre)> faces, in Aabb bounds)
    {
        double spanX = bounds.Max.X - bounds.Min.X;
        double spanY = bounds.Max.Y - bounds.Min.Y;
        double scaleX = spanX > 0 ? 65535.0 / spanX : 0;   // exact-zero guard: a degenerate
        double scaleY = spanY > 0 ? 65535.0 / spanY : 0;   // span puts everything in bucket 0
        double minX = bounds.Min.X, minY = bounds.Min.Y;

        var keys = new uint[faces.Count];
        var order = new List<int>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            uint x = (uint)Math.Clamp((faces[i].Centre.X - minX) * scaleX, 0, Morton2d.MaxCoordinate);
            uint y = (uint)Math.Clamp((faces[i].Centre.Y - minY) * scaleY, 0, Morton2d.MaxCoordinate);
            // Morton2d owns the interleave: EngrCAD.Core.Geometry2.SpaceFillingCurve's Z-order
            // member walks the same codes, and two spellings of one bijection would let the
            // silhouette fold's merge order and that curve's visit order silently disagree.
            keys[i] = Morton2d.Encode(x, y);
            order.Add(i);
        }
        order.Sort((p, q) => keys[p].CompareTo(keys[q]));
        return order;
    }

    // ---- mesh helpers ----

    private static bool StraddlesPlane(HalfEdgeMesh mesh, in Vector3d origin, in Vector3d normal)
    {
        bool above = false, below = false;
        var unit = normal.Normalized(Tolerance.Default);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            double d = (mesh.GetPosition(v) - origin).Dot(unit);
            above |= d > Tolerance.Default.Linear;
            below |= d < -Tolerance.Default.Linear;
            if (above && below)
                return true;
        }
        return false;
    }

    // ---- B-Rep helpers ----

    private static bool BoundsStraddle(in Aabb bounds, in Vector3d origin, in Vector3d normal)
    {
        // Signed distance range of a box to a plane: the centre distance plus/minus the
        // box's extent projected on the normal.
        var centre = bounds.Center;
        var half = bounds.Size * 0.5;
        double distance = (centre - origin).Dot(normal);
        double reach = Math.Abs(half.X * normal.X) + Math.Abs(half.Y * normal.Y) + Math.Abs(half.Z * normal.Z);
        return Math.Abs(distance) <= reach;
    }

    private static void RejectFlushFace(BrepFace face, in Vector3d origin, in Vector3d normal)
    {
        if (!face.IsPlanar(out var faceOrigin, out var faceNormal))
            return;
        if (!faceNormal.IsParallelTo(normal, Tolerance.Default))
            return;
        if (Math.Abs((faceOrigin - origin).Dot(normal)) > Tolerance.Default.Linear)
            return;
        throw new NotSupportedException(
            "The section plane is flush with a planar face, so the cross-section there is an area rather than a curve. "
            + "Offset the plane off the face.");
    }

    /// <summary>
    /// An edge lying WHOLLY in the section plane (a sphere's equator cut at the equator, a
    /// cylinder's rim cut at the rim) leaves the section curve sitting exactly on both
    /// adjacent faces' trim boundaries, where the midpoint probe is a coin flip. Say so
    /// rather than returning an arbitrary answer.
    /// </summary>
    private static void RejectInPlaneEdge(BrepEdge edge, in Vector3d origin, in Vector3d normal, double weld)
    {
        const int probes = 5;
        for (int i = 0; i <= probes; i++)
        {
            var point = edge.Curve.PointAt(edge.Domain.ParameterAt(i / (double)probes));
            if (Math.Abs((point - origin).Dot(normal)) > weld)
                return;
        }
        throw new NotSupportedException(
            "The section plane contains a whole edge of the solid, so the cross-section runs along the boundary "
            + "of two faces at once. Offset the plane off the edge.");
    }

    /// <summary>
    /// Parameters where an edge's curve crosses the plane, appended as 3D points. Sign
    /// changes are bracketed on a dense scan and refined by bisection on the exact curve
    /// (a root solve, never a distance minimization — see the STEP reader's note); a sample
    /// already on the plane is taken verbatim.
    /// </summary>
    private static void CollectPlaneCrossings(
        BrepEdge edge, in Vector3d origin, in Vector3d normal, double weld, List<Vector3d> into)
    {
        var curve = edge.Curve;
        var domain = edge.Domain;
        var o = origin;
        var n = normal;
        double Signed(double t) => (curve.PointAt(t) - o).Dot(n);

        const int scan = 64;
        double previousT = domain.Start;
        double previousD = Signed(previousT);
        AddNode(into, curve.PointAt(previousT), weld, previousD, o, n);
        for (int i = 1; i <= scan; i++)
        {
            double t = domain.ParameterAt(i / (double)scan);
            double d = Signed(t);
            if (Math.Abs(d) <= weld)
            {
                AddNode(into, curve.PointAt(t), weld, d, o, n);
            }
            else if (Math.Abs(previousD) > weld && previousD * d < 0)
            {
                double lo = previousT, hi = t, dlo = previousD;
                for (int step = 0; step < 80; step++)
                {
                    double mid = 0.5 * (lo + hi);
                    double dm = Signed(mid);
                    // Exact-zero test: a bisection that lands on the root is done.
                    if (dm == 0)
                    {
                        lo = hi = mid;
                        break;
                    }
                    if (dlo * dm < 0)
                    {
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                        dlo = dm;
                    }
                }
                var point = curve.PointAt(0.5 * (lo + hi));
                AddNode(into, point, weld, 0, o, n);
            }
            previousT = t;
            previousD = d;
        }
    }

    private static void AddNode(
        List<Vector3d> into, in Vector3d point, double weld, double signedDistance,
        in Vector3d origin, in Vector3d normal)
    {
        if (Math.Abs(signedDistance) > weld && Math.Abs((point - origin).Dot(normal)) > weld)
            return;
        foreach (var existing in into)
        {
            if (existing.DistanceTo(point) <= weld)
                return;
        }
        into.Add(point);
    }

    /// <summary>
    /// Splits one face-plane intersection curve at the node points that lie on it, keeps the
    /// pieces whose MIDPOINT is inside the face, and emits each kept piece as a flattened
    /// polyline (closed pieces go straight into <paramref name="loops"/>).
    /// </summary>
    private static void CollectFaceRuns(
        Curve3d curve, BrepFace face, IReadOnlyList<int> faceNodes, IReadOnlyList<Vector3d> nodes,
        double chordTolerance, double weld,
        List<IReadOnlyList<Vector2d>> loops, List<SectionRun> openRuns, in Frame3d plane)
    {
        var samples = SampleParameters(curve, chordTolerance);
        var cuts = new List<(double T, int Node)>();
        foreach (int node in faceNodes)
        {
            if (TryParameterOf(curve, nodes[node], samples, weld, out double t))
                cuts.Add((t, node));
        }
        cuts.Sort((a, b) => a.T.CompareTo(b.T));

        var domain = curve.Domain;
        bool closed = curve.PointAt(domain.Start).DistanceTo(curve.PointAt(domain.End)) <= weld;

        if (cuts.Count == 0)
        {
            // No node touches this curve, so it cannot leave the face part way: it is wholly
            // in or wholly out, decided by one probe.
            if (!InsideFace(face, curve.PointAt(domain.ParameterAt(0.5))))
                return;
            var whole = Flatten(curve, domain.Start, domain.End, samples, null, null);
            if (closed)
                loops.Add(ToPlane(whole, plane));
            else
                openRuns.Add(new SectionRun(whole, -1, -1));
            return;
        }

        // Piece boundaries: the cuts, plus the domain ends for an open curve. A closed curve
        // wraps from its last cut back to its first.
        var breaks = new List<(double T, int Node)>(cuts);
        if (!closed)
        {
            breaks.Insert(0, (domain.Start, -1));
            breaks.Add((domain.End, -1));
        }

        int pieces = closed ? breaks.Count : breaks.Count - 1;
        for (int i = 0; i < pieces; i++)
        {
            var (start, startNode) = breaks[i];
            var (end, endNode) = closed ? breaks[(i + 1) % breaks.Count] : breaks[i + 1];
            bool wraps = closed && i == breaks.Count - 1;
            if (!wraps && end - start <= weld)
                continue;
            // The probe sits at the piece's MIDPOINT, never at an end (which is on the trim
            // boundary, where containment is a tie).
            double probe = wraps
                ? WrapParameter(domain, start + 0.5 * (domain.Length - (start - end)))
                : 0.5 * (start + end);
            if (!InsideFace(face, curve.PointAt(probe)))
                continue;

            // Endpoints are the NODE POSITIONS, not the curve re-evaluated at a searched
            // parameter: the parameter carries the search's residual (~5e-11 for a ternary
            // search), while the node is the exact shared crossing both faces agree on.
            var startPoint = startNode >= 0 ? nodes[startNode] : (Vector3d?)null;
            var endPoint = endNode >= 0 ? nodes[endNode] : (Vector3d?)null;
            List<Vector3d> run;
            if (wraps)
            {
                run = Flatten(curve, start, domain.End, samples, startPoint, null);
                run.RemoveAt(run.Count - 1);
                run.AddRange(Flatten(curve, domain.Start, end, samples, null, endPoint).Skip(1));
            }
            else
            {
                run = Flatten(curve, start, end, samples, startPoint, endPoint);
            }
            if (run.Count >= 2)
                openRuns.Add(new SectionRun(run, startNode, endNode));
        }
    }

    /// <summary>An open piece of the section: its polyline plus the node index at each end
    /// (-1 when the piece does not terminate on an edge crossing).</summary>
    private readonly record struct SectionRun(List<Vector3d> Points, int StartNode, int EndNode);

    /// <summary>
    /// Is a point on a face's surface inside the face's trimmed region? The two-sided parity
    /// rule — see <see cref="FaceGeometry.ContainsTwoSided"/>, which this page's pole finding
    /// (a sphere's cross-section came back empty) is the origin of, and which the boolean's
    /// carrier clip now asks as well.
    /// </summary>
    private static bool InsideFace(BrepFace face, in Vector3d point) =>
        FaceGeometry.ContainsTwoSided(face, point);

    private static double WrapParameter(in Interval domain, double t)
    {
        double length = domain.Length;
        while (t > domain.End)
            t -= length;
        while (t < domain.Start)
            t += length;
        return t;
    }

    /// <summary>Sample parameters honouring the one sampling rule that matters: a tracer
    /// polyline is on-surface only at its VERTICES, so it is sampled there and nowhere
    /// else. Everything else is sampled uniformly at a density the chord tolerance sets.</summary>
    private static double[] SampleParameters(Curve3d curve, double chordTolerance)
    {
        if (Underlying(curve) is PolylineCurve3d polyline)
            return [.. polyline.VertexParameters];

        var domain = curve.Domain;
        // A straight section curve gets exactly one segment: interior samples on a line are
        // collinear in exact arithmetic but NOT after projection into the plane's basis, so
        // Region2d.WithoutCollinearVertices cannot drop them and a box section comes back
        // with eight corners instead of four.
        int count = SegmentsFor(curve, chordTolerance);
        if (count > 1)
            count = Math.Max(MinimumCurveSamples, count);
        var parameters = new double[count + 1];
        for (int i = 0; i <= count; i++)
            parameters[i] = domain.ParameterAt(i / (double)count);
        return parameters;
    }

    private static Curve3d Underlying(Curve3d curve) =>
        curve switch
        {
            ReversedCurve reversed => Underlying(reversed.Underlying),
            TransformedCurve transformed => Underlying(transformed.Underlying),
            _ => curve,
        };

    /// <summary>Segments so that no chord bulges more than the tolerance: exact for circles
    /// and ellipses (sagitta of a circular arc), a curvature-free fallback otherwise.</summary>
    private static int SegmentsFor(Curve3d curve, double chordTolerance)
    {
        double radius = Underlying(curve) switch
        {
            Circle3d circle => circle.Radius,
            Ellipse3d ellipse => Math.Max(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length),
            Line3d => 0,
            _ => double.NaN,
        };
        // Deliberate exact-zero test: a straight generator needs exactly one segment.
        if (radius == 0)
            return 1;
        if (double.IsNaN(radius))
            return 64;
        double span = curve.Domain.Length;
        double sweep = Underlying(curve) is Circle3d ? span : 2 * Math.PI;
        double ratio = 1 - chordTolerance / radius;
        double maxAngle = ratio <= -1 ? Math.PI : 2 * Math.Acos(Math.Max(ratio, -1));
        return Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / maxAngle));
    }

    /// <summary>Curve parameter of a point known to lie on the curve: nearest sample, then a
    /// few ternary-search refinements. Returns false when the point is not on this curve.</summary>
    private static bool TryParameterOf(
        Curve3d curve, in Vector3d point, double[] samples, double weld, out double parameter)
    {
        parameter = 0;
        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < samples.Length; i++)
        {
            double distance = curve.PointAt(samples[i]).DistanceTo(point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        if (best < 0)
            return false;

        double lo = samples[Math.Max(0, best - 1)];
        double hi = samples[Math.Min(samples.Length - 1, best + 1)];
        var target = point;
        double Distance(double t) => curve.PointAt(t).DistanceTo(target);
        for (int step = 0; step < 60 && hi - lo > 0; step++)
        {
            double a = lo + (hi - lo) / 3;
            double b = hi - (hi - lo) / 3;
            if (Distance(a) < Distance(b))
                hi = b;
            else
                lo = a;
        }
        parameter = 0.5 * (lo + hi);
        return Distance(parameter) <= Math.Max(weld, 1e-7);
    }

    /// <summary>The curve between two parameters as a polyline: the given sample grid
    /// restricted to the span. The end points are the supplied exact positions when there
    /// are any (node crossings), otherwise the curve evaluated at the span ends.</summary>
    private static List<Vector3d> Flatten(
        Curve3d curve, double start, double end, double[] samples,
        Vector3d? startPoint, Vector3d? endPoint)
    {
        var points = new List<Vector3d> { startPoint ?? curve.PointAt(start) };
        double low = Math.Min(start, end), high = Math.Max(start, end);
        if (start <= end)
        {
            foreach (double t in samples)
            {
                if (t > low && t < high)
                    points.Add(curve.PointAt(t));
            }
        }
        else
        {
            for (int i = samples.Length - 1; i >= 0; i--)
            {
                if (samples[i] > low && samples[i] < high)
                    points.Add(curve.PointAt(samples[i]));
            }
        }
        points.Add(endPoint ?? curve.PointAt(end));
        return points;
    }

    private static IReadOnlyList<Vector2d> ToPlane(IReadOnlyList<Vector3d> points, in Frame3d plane)
    {
        var flat = new Vector2d[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var local = plane.ToLocal(points[i]);
            flat[i] = new Vector2d(local.X, local.Y);
        }
        return flat;
    }

    /// <summary>
    /// Joins the per-face runs into closed loops. Every run ends on a node, and nodes are
    /// shared exactly between the two faces of an edge, so runs are keyed by NODE INDEX and
    /// chained by integer identity — no distance weld, no accumulated drift.
    /// </summary>
    private static void ChainRuns(
        List<SectionRun> runs, in Frame3d plane, List<IReadOnlyList<Vector2d>> loops)
    {
        if (runs.Count == 0)
            return;

        var byNode = new Dictionary<int, List<int>>();
        for (int i = 0; i < runs.Count; i++)
        {
            foreach (int node in new[] { runs[i].StartNode, runs[i].EndNode })
            {
                if (node < 0)
                    continue;
                if (!byNode.TryGetValue(node, out var list))
                    byNode[node] = list = [];
                list.Add(i);
            }
        }

        var used = new bool[runs.Count];
        for (int seed = 0; seed < runs.Count; seed++)
        {
            if (used[seed])
                continue;
            var chain = new List<Vector3d>(runs[seed].Points);
            used[seed] = true;
            int startNode = runs[seed].StartNode;
            int currentNode = runs[seed].EndNode;

            while (currentNode >= 0 && currentNode != startNode)
            {
                int next = -1;
                if (byNode.TryGetValue(currentNode, out var candidates))
                {
                    foreach (int candidate in candidates)
                    {
                        if (!used[candidate])
                        {
                            next = candidate;
                            break;
                        }
                    }
                }
                if (next < 0)
                    break;
                used[next] = true;
                bool forward = runs[next].StartNode == currentNode;
                var points = runs[next].Points;
                for (int i = 1; i < points.Count; i++)
                    chain.Add(forward ? points[i] : points[points.Count - 1 - i]);
                currentNode = forward ? runs[next].EndNode : runs[next].StartNode;
            }

            // A chain that closed drops its duplicated last point (loops close implicitly);
            // one that did not is an open section span and is discarded rather than faked.
            if (currentNode != startNode || chain.Count < 4)
                continue;
            chain.RemoveAt(chain.Count - 1);
            loops.Add(Region2d.WithoutCollinearVertices(ToPlane(chain, plane)));
        }
    }
}
