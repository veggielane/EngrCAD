using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// B-Rep → mesh conversion: each edge is sampled once into a shared polyline, faces are
/// triangulated against those polylines (so neighboring faces meet exactly), and the
/// resulting soup is welded into a half-edge mesh. Planar faces (any number of loops)
/// ear-clip in plane coordinates; cylinder bands and full-domain generated faces
/// (extruded/revolved/swept) tessellate as parameter grids; trimmed faces on those
/// surfaces — loops not covering the natural grid domain, e.g. fragments from
/// <see cref="FaceSplitter.SplitByCurve"/> or a mitered rim-fillet band — go through
/// <see cref="TrimmedFaceTessellator"/>. Trimmed NURBS faces are future work.
///
/// A trimmed face the trimmed path cannot handle REFUSES, naming the face, the sample
/// counts and the reason. It used to fall through to the surface's natural grid, which
/// covers the whole parameter rectangle rather than the trimmed face: not merely coarse
/// but the wrong geometry, welding into an open mesh with no complaint — the same silent
/// failure mode `BrepBoolean.Verified` exists to catch on the boolean side.
/// </summary>
public static class BRepTessellator
{
    /// <summary>
    /// Tessellates a B-Rep solid into a welded triangle mesh.
    /// </summary>
    /// <param name="progress">
    /// Optional progress + cooperative cancellation, polled at EDGE and FACE boundaries —
    /// the coarse checkpoints, since a single trimmed face is one indivisible ear-clipping
    /// job. Cancellation throws <see cref="OperationCanceledException"/> and no partial
    /// mesh is returned.
    /// <para><b>Never pass a cancellable progress from inside a cached lowering.</b> This
    /// is safe to cancel because its own result is thrown away wholesale; the rule the
    /// document model learned the hard way is that abandoning work whose result is CACHED
    /// (a <c>Shape</c>'s lowered <c>BrepSolid</c>) leaves the cache claiming a lowering it
    /// never produced. Tessellating an already-cached solid is downstream of that, so it
    /// may observe the token; the lowering that produced the solid may not.</para>
    /// </param>
    public static HalfEdgeMesh Tessellate(
        BrepSolid solid, int segmentsPerCircle = 32, int curveSamples = 24,
        ProgressCancel? progress = null)
    {
        if (segmentsPerCircle < 3) throw new ArgumentOutOfRangeException(nameof(segmentsPerCircle));
        if (curveSamples < 2) throw new ArgumentOutOfRangeException(nameof(curveSamples));

        // Coarse phase weights, honest rather than precise: sampling every edge, then the
        // faces (much the larger share — trimmed faces ear-clip and refine), then one
        // indivisible weld of the whole polygon soup.
        const double EdgePhase = 0.15;
        const double FacePhase = 0.75;

        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        int edgeCount = solid.Edges.Count();
        int edgesDone = 0;
        foreach (var edge in solid.Edges)
        {
            progress?.ThrowIfCancelled();
            edgePolylines[edge] = SampleEdge(edge, segmentsPerCircle, curveSamples);
            progress?.Report(EdgePhase * ++edgesDone / Math.Max(1, edgeCount));
        }

        var polygons = new List<IReadOnlyList<Vector3d>>();
        int faceCount = solid.Faces.Count();
        int facesDone = 0;
        foreach (var face in solid.Faces)
        {
            progress?.ThrowIfCancelled();
            TessellateFace(face, edgePolylines, segmentsPerCircle, curveSamples, polygons);
            progress?.Report(EdgePhase + FacePhase * ++facesDone / Math.Max(1, faceCount));
        }

        progress?.ThrowIfCancelled();
        // Zip seams: cap triangulation may merge exactly-collinear boundary runs (earcut
        // filters them), leaving T-junctions against the finer neighboring faces; zipping
        // reinserts the missing vertices so the mesh closes.
        // 1e-9 = Tolerance.Default.Linear: the absolute weld tolerance — geometry that
        // must weld is constructed exactly, so this must NOT be loosened to hide cracks.
        var mesh = MeshWelder.WeldPolygons(polygons, tolerance: 1e-9, zipSeams: true);
        progress?.Report(1);
        return mesh;
    }

    /// <summary>
    /// Appends one face's polygons, routing it to the path its surface and trim state
    /// call for, and flipping reversed faces (boolean output points opposite its surface
    /// normal). Split out of <see cref="Tessellate"/> so the same routing can be replayed
    /// per face by <see cref="TessellateByFace"/>.
    /// </summary>
    private static void TessellateFace(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        int segmentsPerCircle,
        int curveSamples,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        int firstPolygon = polygons.Count;
        switch (face.Surface)
        {
            case PlaneSurface plane:
                TessellatePlanarFace(face, plane, edgePolylines, polygons);
                break;
            case CylinderSurface when IsCylinderBand(face):
                TessellateCylinderBand(face, edgePolylines, polygons);
                break;
            case CylinderSurface:
                if (!TrimmedFaceTessellator.TryTessellate(
                        face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out string? cylinderFailure))
                    throw new NotSupportedException(
                        "Cylindrical faces must be full two-ring bands or trimmed regions with non-wrapping loops. " +
                        Diagnose(face, edgePolylines, segmentsPerCircle, curveSamples, cylinderFailure));
                break;
            case HelicalSurface helical:
                TessellateHelicalBand(face, helical, edgePolylines, polygons);
                break;
            case ExtrudedSurface or RevolvedSurface or SweptSurface or LoftedSurface:
            {
                var (uParams, vParams, closedU, closedV) = GridParams(face.Surface, segmentsPerCircle, curveSamples);
                // Full-domain faces (the factories' and wrap-splitter's output) keep
                // the grid path — its samples coincide with the shared edge polylines.
                // Faces whose loops don't cover the domain go through the trimmed
                // path, and a failure there REFUSES: the grid would cover the whole
                // parameter rectangle, which is not this face, so it would silently
                // hand back an open mesh (the worst failure mode this project has).
                if (IsFullDomainFace(face, edgePolylines, uParams, vParams, closedU, closedV))
                {
                    TessellateGrid(face.Surface, uParams, vParams, closedU, closedV, polygons);
                }
                else if (!TrimmedFaceTessellator.TryTessellate(
                             face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out string? failure))
                {
                    throw new NotSupportedException(
                        "A trimmed face could not be tessellated, and its surface's natural grid covers more " +
                        "than the face, so falling back to it would produce an open mesh. " +
                        Diagnose(face, edgePolylines, segmentsPerCircle, curveSamples, failure));
                }
                break;
            }
            default:
                throw new NotSupportedException(
                    $"Tessellation of {face.Surface.GetType().Name} faces is not implemented yet.");
        }

        // Reversed faces (boolean output) point opposite their surface normal.
        if (face.IsReversed)
        {
            for (int i = firstPolygon; i < polygons.Count; i++)
                polygons[i] = Reversed(polygons[i]);
        }
    }

    /// <summary>
    /// The same polygon wound the other way, KEEPING its first vertex — <c>[a, d, c, b]</c>
    /// for <c>[a, b, c, d]</c>, not <c>[d, c, b, a]</c>.
    /// <para>Both are the same cyclic polygon with the opposite orientation, so for the
    /// winding this is a free choice; for the GEOMETRY it is not. A quad is triangulated
    /// downstream by fanning from vertex 0, so <c>[a, b, c, d]</c> is split along a–c and
    /// <c>[d, c, b, a]</c> along b–d — the other diagonal. On a grid cell that is skewed
    /// and non-planar those two triangulations are not equally good, and a plain
    /// <c>Reverse()</c> silently picks the wrong one for every subtracted tool's face.
    /// <para>Measured on an M8 B-Rep threaded hole (a subtracted helical tool, whose sheared
    /// grid gives cells with a diagonal ratio up to 40:1): 5 544 of 30 912 facets faced
    /// INWARD and the worst facet-vs-surface normal agreement was −0.163, against zero
    /// folds and 0.99976 for the identical geometry unsubtracted (a threaded rod). Rotating
    /// the reversal so vertex 0 stays put is the whole fix.</para></para>
    /// </summary>
    private static IReadOnlyList<Vector3d> Reversed(IReadOnlyList<Vector3d> polygon)
    {
        var reversed = new Vector3d[polygon.Count];
        reversed[0] = polygon[0];
        for (int i = 1; i < polygon.Count; i++)
            reversed[i] = polygon[polygon.Count - i];
        return reversed;
    }

    /// <summary>
    /// The same tessellation <see cref="Tessellate"/> performs, but returned per face and
    /// UNWELDED — the seam a facet-quality audit needs, since only the owning face knows
    /// which surface a triangle is supposed to approximate. Welding is what destroys that
    /// attribution, so this stops one step short of it.
    /// <para>Internal: this is a diagnostic seam for tests, not a second public conversion
    /// route. It must stay a pure factoring of the production path (same routing, same
    /// reversal flip) or a quality assertion built on it would audit geometry no consumer
    /// ever sees.</para>
    /// </summary>
    internal static List<(BrepFace Face, List<IReadOnlyList<Vector3d>> Polygons)> TessellateByFace(
        BrepSolid solid, int segmentsPerCircle = 32, int curveSamples = 24)
    {
        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = SampleEdge(edge, segmentsPerCircle, curveSamples);

        var byFace = new List<(BrepFace, List<IReadOnlyList<Vector3d>>)>();
        foreach (var face in solid.Faces)
        {
            var polygons = new List<IReadOnlyList<Vector3d>>();
            TessellateFace(face, edgePolylines, segmentsPerCircle, curveSamples, polygons);
            byFace.Add((face, polygons));
        }
        return byFace;
    }

    /// <summary>
    /// Locates a face for a refusal message: its surface type, where it sits, how its
    /// loops are shaped, the sample counts in force (the count is part of the story —
    /// some failures only appear at high densities) and the tessellator's own reason.
    /// </summary>
    private static string Diagnose(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        int segmentsPerCircle,
        int curveSamples,
        string? failure)
    {
        var loops = face.Loops
            .Select(l => $"{l.Coedges.Count} coedge(s)/{LoopPolyline(l, edgePolylines).Count} samples");
        var anchor = face.Loops.Count > 0 && face.OuterLoop.Coedges.Count > 0
            ? face.OuterLoop.Coedges[0].Edge.Curve.PointAt(face.OuterLoop.Coedges[0].Edge.Domain.Start).ToString()
            : "unknown";
        return $"Face: {face.Surface.GetType().Name}{(face.IsReversed ? " (reversed)" : "")} at {anchor}, " +
            $"loops [{string.Join(", ", loops)}], at segmentsPerCircle={segmentsPerCircle}, " +
            $"curveSamples={curveSamples}. Reason: {failure ?? "unknown"}.";
    }

    internal static List<Vector3d> SampleEdge(BrepEdge edge, int segmentsPerCircle, int curveSamples)
    {
        var domain = edge.Domain;

        // Marching-tracer polylines lie on their surfaces only at their vertices
        // (chordal between): sample exactly those, or the trimmed path's inverse
        // evaluation would reject mid-chord samples as off-surface.
        if (edge.Curve is PolylineCurve3d polyline)
        {
            var points = new List<Vector3d> { edge.Curve.PointAt(domain.Start) };
            foreach (double t in polyline.VertexParameters)
            {
                // Parameter-space interiority guard (round-off scale): endpoint samples
                // are added separately and must not duplicate.
                if (t > domain.Start + 1e-12 && t < domain.End - 1e-12)
                    points.Add(edge.Curve.PointAt(t));
            }
            if (!edge.IsClosedEdge)
                points.Add(edge.Curve.PointAt(domain.End));
            return points;
        }

        // Helix rails and their cap-plane spiral cuts sample proportionally to their
        // turning angle (the parameter IS the angle for both types): a rail spanning N
        // turns gets N·segmentsPerCircle segments, a cut spanning a fraction of a turn
        // the matching fraction. Helical band grids derive their column/row counts from
        // these same polylines, so the sampling agrees by construction.
        if (edge.Curve.Underlying is Helix3d or SpiralArc3d)
        {
            int n = AngularSegments(domain.Length, segmentsPerCircle);
            var points = new List<Vector3d>(n + 1);
            for (int i = 0; i <= n; i++)
                points.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / n)));
            return points;
        }

        if (edge.IsClosedEdge)
        {
            int n = edge.Curve.Underlying is Circle3d ? segmentsPerCircle : curveSamples;
            var points = new List<Vector3d>(n);
            for (int i = 0; i < n; i++)
                points.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / n)));
            return points;
        }

        if (edge.Curve.Underlying is Line3d)
            return [edge.Curve.PointAt(domain.Start), edge.Curve.PointAt(domain.End)];

        var samples = new List<Vector3d>(curveSamples + 1);
        for (int i = 0; i <= curveSamples; i++)
            samples.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / curveSamples)));
        return samples;
    }

    /// <summary>
    /// Parameter samples over a curve's full domain, matching <see cref="SampleEdge"/>'s
    /// rules exactly so face grids and shared boundary edges weld without cracks.
    /// </summary>
    private static double[] CurveParams(Curve3d curve, int segmentsPerCircle, int curveSamples)
    {
        var domain = curve.Domain;
        if (curve.IsClosed)
        {
            int n = curve.Underlying is Circle3d ? segmentsPerCircle : curveSamples;
            return EvenParams(domain, n, includeEnd: false);
        }
        if (curve.Underlying is Line3d)
            return [domain.Start, domain.End];
        return EvenParams(domain, curveSamples, includeEnd: true);
    }

    private static double[] EvenParams(in Interval domain, int segments, bool includeEnd)
    {
        var parameters = new double[includeEnd ? segments + 1 : segments];
        for (int i = 0; i < parameters.Length; i++)
            parameters[i] = domain.ParameterAt((double)i / segments);
        return parameters;
    }

    /// <summary>
    /// The natural grid sampling for a generated surface. Full turns are periodic in u;
    /// partial revolutions must sample u the same way their arc rail edges do
    /// (curveSamples), so the boundaries weld. A closed generator (e.g. a revolved
    /// circle = pipe elbow) is periodic in v.
    /// </summary>
    private static (double[] U, double[] V, bool ClosedU, bool ClosedV) GridParams(
        Surface surface, int segmentsPerCircle, int curveSamples) => surface switch
    {
        ExtrudedSurface extruded => (
            CurveParams(extruded.Generator, segmentsPerCircle, curveSamples),
            [0.0, 1.0],
            extruded.Generator.IsClosed, false),
        RevolvedSurface revolved => (
            revolved.IsFullTurn
                ? EvenParams(revolved.DomainU, segmentsPerCircle, includeEnd: false)
                : EvenParams(revolved.DomainU, curveSamples, includeEnd: true),
            CurveParams(revolved.Generator, segmentsPerCircle, curveSamples),
            revolved.IsFullTurn, revolved.Generator.IsClosed),
        SweptSurface swept => (
            CurveParams(swept.Generator, segmentsPerCircle, curveSamples),
            EvenParams(swept.Path.Domain, curveSamples, includeEnd: true),
            swept.Generator.IsClosed, false),
        // A loft's u boundaries ARE its section curves and its v boundaries its rails, so
        // the surface owns the u rule (see LoftedSurface.NaturalUSegments, which mirrors
        // SampleEdge) and v takes the generic curve density the rails are sampled at.
        LoftedSurface lofted => (
            EvenParams(lofted.DomainU, lofted.NaturalUSegments(segmentsPerCircle, curveSamples),
                includeEnd: !lofted.IsClosedU),
            EvenParams(lofted.DomainV, curveSamples, includeEnd: true),
            lofted.IsClosedU, false),
        _ => throw new NotSupportedException($"No grid sampling for {surface.GetType().Name}."),
    };

    private static bool IsCylinderBand(BrepFace face) =>
        face.Loops.Count == 2 &&
        face.Loops.All(l => l.Coedges.Count == 1 && l.Coedges[0].Edge.IsClosedEdge);

    /// <summary>
    /// Whether the face's loops sample exactly the surface's natural grid boundary — the
    /// invariant grid tessellation relies on to weld against neighboring faces. Compared
    /// two-sided in 3D: full-domain faces (factory output, wrap-split sub-bands) match to
    /// weld tolerance by construction, while trimmed fragments differ by whole samples.
    /// Degenerate boundary runs (pole rings of axis-touching revolves) need no matching
    /// loop.
    /// </summary>
    private static bool IsFullDomainFace(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        double[] uParams, double[] vParams, bool closedU, bool closedV)
    {
        // Ladder seam tier: loop samples vs the natural grid boundary agree to
        // tessellation error, not weld precision; 1e-18 = (1e-9)² is the squared weld
        // tolerance for the all-points-coincident pole test.
        const double tolerance = FaceGeometry.SeamTolerance;
        var surface = face.Surface;
        var boundary = new List<Vector3d>();

        void AddRun(IEnumerable<Vector3d> samples)
        {
            var run = samples.ToList();
            if (run.All(p => p.DistanceSquaredTo(run[0]) <= 1e-18))
                return; // a pole: the whole run is one point, no loop bounds it
            boundary.AddRange(run);
        }

        if (!closedV)
        {
            AddRun(uParams.Select(u => surface.PointAt(u, vParams[0])));
            AddRun(uParams.Select(u => surface.PointAt(u, vParams[^1])));
        }
        if (!closedU)
        {
            AddRun(vParams.Select(v => surface.PointAt(uParams[0], v)));
            AddRun(vParams.Select(v => surface.PointAt(uParams[^1], v)));
        }
        if (boundary.Count == 0)
            return false;

        var loopPoints = face.Loops.SelectMany(l => LoopPolyline(l, edgePolylines)).ToList();
        return Covers(loopPoints, boundary, tolerance) && Covers(boundary, loopPoints, tolerance);
    }

    /// <summary>Every point of <paramref name="subset"/> lies within tolerance of some point of <paramref name="of"/>.</summary>
    private static bool Covers(List<Vector3d> subset, List<Vector3d> of, double tolerance) =>
        subset.All(p => of.Any(q => q.DistanceSquaredTo(p) <= tolerance * tolerance));

    /// <summary>
    /// Full-domain grid tessellation for generated surfaces (extrusions, revolutions,
    /// sweeps). Quads are emitted in (+u, +v) order, i.e. counter-clockwise around
    /// ∂u × ∂v, which the modeling operations arrange to point outward.
    /// </summary>
    private static void TessellateGrid(
        Surface surface, double[] uParams, double[] vParams, bool closedU, bool closedV,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        int nu = uParams.Length;
        int nv = vParams.Length;
        var grid = new Vector3d[nu, nv];
        for (int j = 0; j < nu; j++)
        {
            for (int k = 0; k < nv; k++)
                grid[j, k] = surface.PointAt(uParams[j], vParams[k]);
        }

        int columns = closedU ? nu : nu - 1;
        int rows = closedV ? nv : nv - 1;
        for (int j = 0; j < columns; j++)
        {
            int j1 = (j + 1) % nu;
            for (int k = 0; k < rows; k++)
            {
                int k1 = (k + 1) % nv;
                AddGridCell(polygons, grid[j, k], grid[j1, k], grid[j1, k1], grid[j, k1]);
            }
        }
    }

    /// <summary>
    /// Emits one grid cell, dropping repeated corners. Cells against a degenerate
    /// surface row (a revolved generator touching the axis — sphere poles) collapse to
    /// triangles; fully collapsed cells are skipped.
    /// </summary>
    private static void AddGridCell(
        List<IReadOnlyList<Vector3d>> polygons,
        in Vector3d a, in Vector3d b, in Vector3d c, in Vector3d d)
    {
        Span<Vector3d> corners = [a, b, c, d];
        var distinct = new List<Vector3d>(4);
        for (int i = 0; i < corners.Length; i++)
        {
            if (!corners[i].AreEqual(corners[(i + 1) % corners.Length], Tolerance.Default))
                distinct.Add(corners[i]);
        }
        if (distinct.Count >= 3)
            polygons.Add(distinct);
    }

    internal static List<Vector3d> LoopPolyline(BrepLoop loop, Dictionary<BrepEdge, List<Vector3d>> edgePolylines)
    {
        var points = new List<Vector3d>();
        foreach (var coedge in loop.Coedges)
        {
            var polyline = edgePolylines[coedge.Edge];
            IEnumerable<Vector3d> ordered = coedge.SameSense ? polyline : Enumerable.Reverse(polyline);
            if (coedge.Edge.IsClosedEdge)
            {
                points.AddRange(ordered); // closed polyline carries no duplicate endpoint
            }
            else
            {
                // Open polylines include both endpoints; drop the last so consecutive
                // coedges share their junction vertex once.
                var list = ordered.ToList();
                points.AddRange(list.Take(list.Count - 1));
            }
        }
        return points;
    }

    private static void TessellatePlanarFace(
        BrepFace face, PlaneSurface plane,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        // Triangulator output is CCW in plane coordinates, whose 3D normal is
        // x × y = the plane normal = the outward face normal by construction.
        var boundary = LoopPolyline(face.OuterLoop, edgePolylines);
        var boundary2d = boundary.Select(p => plane.Project(p)).ToList();

        if (face.Loops.Count == 1)
        {
            foreach (var (a, b, c) in PolygonTriangulator.Triangulate(boundary2d))
                polygons.Add([boundary[a], boundary[b], boundary[c]]);
            return;
        }

        // Holes: triangle indices refer to [outer..., hole0..., hole1...].
        var combined = new List<Vector3d>(boundary);
        var holes2d = new List<IReadOnlyList<Vector2d>>();
        foreach (var loop in face.Loops.Skip(1))
        {
            var hole = LoopPolyline(loop, edgePolylines);
            combined.AddRange(hole);
            holes2d.Add(hole.Select(p => plane.Project(p)).ToList());
        }
        foreach (var (a, b, c) in PolygonTriangulator.TriangulateWithHoles(boundary2d, holes2d))
            polygons.Add([combined[a], combined[b], combined[c]]);
    }

    /// <summary>Segments for an angular span at the given circle density; the epsilon
    /// guards Ceiling at exact integer boundaries (equal spans computed through
    /// different arithmetic may differ by an ulp and must not round apart).</summary>
    private static int AngularSegments(double span, int segmentsPerCircle) =>
        Math.Max(1, (int)Math.Ceiling(Math.Abs(span) * segmentsPerCircle / (2 * Math.PI) - 1e-9));

    /// <summary>
    /// A full helical band — the parallelogram in (u, v) between two helix rails
    /// (v = 0 / v = 1) and two cap-plane spiral cuts. The natural grid is sheared:
    /// columns are iso-axial spiral rungs connecting bottom-rail sample j to top-rail
    /// sample j (each rail spans the same turning angle, offset by the generator's
    /// axial extent), so the first and last columns ARE the cap cuts. All boundary
    /// points are taken verbatim from the shared edge polylines (band↔band and
    /// band↔cap welding is exact by construction); only interior points evaluate the
    /// surface, at parameters interpolated from the exactly projected rail corners.
    /// </summary>
    private static void TessellateHelicalBand(
        BrepFace face,
        HelicalSurface surface,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        var coedges = face.Loops.Count == 1 ? face.OuterLoop.Coedges : null;
        var railEdges = coedges?.Where(c => c.Edge.Curve.Underlying is Helix3d).Select(c => c.Edge).Distinct().ToList();
        var cutEdges = coedges?.Where(c => c.Edge.Curve.Underlying is SpiralArc3d).Select(c => c.Edge).Distinct().ToList();
        if (coedges is null || coedges.Count != 4 || railEdges!.Count != 2 || cutEdges!.Count != 2)
            throw new NotSupportedException(
                "Helical faces must be full bands (one loop: two helix rails + two cap spiral cuts); " +
                "trimmed helical faces are not supported yet.");

        Vector2d Project(Vector3d p)
        {
            if (!surface.TryProjectPoint(p, out var uv, FaceGeometry.InverseEvaluationTolerance))
                throw new InvalidOperationException($"Helical band boundary point {p} does not lie on the band surface.");
            return uv;
        }

        // Rails ordered bottom (v = 0) to top (v = 1); cuts ordered by u so the first
        // is the grid's j = 0 column. Both classifications use the exact inverse
        // evaluation of an interior sample.
        var rails = railEdges
            .OrderBy(e => Project(e.Curve.PointAt(e.Domain.Mid)).Y)
            .Select(e => edgePolylines[e])
            .ToList();
        var cuts = cutEdges
            .OrderBy(e => Project(e.Curve.PointAt(e.Domain.Mid)).X)
            .Select(e =>
            {
                // Reorder each cut polyline by ascending v (rows), whichever way the
                // spiral parameter runs.
                var polyline = edgePolylines[e];
                if (Project(polyline[0]).Y <= Project(polyline[^1]).Y)
                    return polyline;
                var reversed = new List<Vector3d>(polyline);
                reversed.Reverse();
                return reversed;
            })
            .ToList();

        var bottomRail = rails[0];
        var topRail = rails[1];
        int n = bottomRail.Count - 1;
        int m = cuts[0].Count - 1;
        if (topRail.Count != n + 1 || cuts[1].Count != m + 1)
            throw new InvalidOperationException(
                "Helical band boundary polylines disagree in sample count (rails must share their span, cuts theirs).");

        // Exact parameter anchors for interior evaluation: the projected rail corners.
        double uBottomStart = Project(bottomRail[0]).X, uBottomEnd = Project(bottomRail[^1]).X;
        double uTopStart = Project(topRail[0]).X, uTopEnd = Project(topRail[^1]).X;

        var grid = new Vector3d[n + 1, m + 1];
        for (int j = 0; j <= n; j++)
        {
            grid[j, 0] = bottomRail[j];
            grid[j, m] = topRail[j];
        }
        for (int k = 1; k < m; k++)
        {
            grid[0, k] = cuts[0][k];
            grid[n, k] = cuts[1][k];
        }
        for (int j = 1; j < n; j++)
        {
            double f = (double)j / n;
            double uBottom = uBottomStart + (uBottomEnd - uBottomStart) * f;
            double uTop = uTopStart + (uTopEnd - uTopStart) * f;
            for (int k = 1; k < m; k++)
            {
                double v = (double)k / m;
                grid[j, k] = surface.PointAt(uBottom + (uTop - uBottom) * v, v);
            }
        }

        // (+u, +v) cell order — counter-clockwise around ∂u × ∂v, which the builder
        // arranges to point outward (generator traversed with increasing axial
        // coordinate), matching TessellateGrid's convention.
        for (int j = 0; j < n; j++)
        {
            for (int k = 0; k < m; k++)
                AddGridCell(polygons, grid[j, k], grid[j + 1, k], grid[j + 1, k + 1], grid[j, k + 1]);
        }
    }

    private static void TessellateCylinderBand(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        var cylinder = (CylinderSurface)face.Surface;

        // Use the raw circle polylines (u increasing = CCW around the axis) and order the
        // rings bottom-to-top along the axis; the quad winding below is outward for that
        // arrangement.
        var rings = face.Loops
            .Select(l => edgePolylines[l.Coedges[0].Edge])
            .OrderBy(ring => ring.Average(p => (p - cylinder.Origin).Dot(cylinder.Axis)))
            .ToList();
        var bottom = rings[0];
        var top = rings[1];
        if (bottom.Count != top.Count)
            throw new NotSupportedException("Cylinder band rings must share a segment count.");

        int n = bottom.Count;
        for (int j = 0; j < n; j++)
        {
            int j1 = (j + 1) % n;
            polygons.Add([bottom[j], bottom[j1], top[j1], top[j]]);
        }
    }
}
