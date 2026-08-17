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
        ProgressCancel? progress = null, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        TessellateCore(solid, segmentsPerCircle, curveSamples, progress, logger,
            faceProvenance: null, out _);

    /// <summary>
    /// A B-Rep face and the tessellated <see cref="HalfEdgeMesh"/> it lies on, plus a
    /// per-face map back to the solid's faces.
    /// </summary>
    /// <param name="Mesh">The welded mesh — <b>bit-for-bit</b> what <see cref="Tessellate"/>
    /// returns on the same inputs.</param>
    /// <param name="FaceProvenance"><paramref name="Mesh"/>-face-count array: entry <c>f</c>
    /// is the index, in <c>solid.Faces</c> enumeration order, of the <see cref="BrepFace"/>
    /// mesh face <c>f</c> came from.</param>
    public readonly record struct TessellationProvenance(
        HalfEdgeMesh Mesh, IReadOnlyList<int> FaceProvenance);

    /// <summary>
    /// Tessellates <paramref name="solid"/> and, beside the mesh, reports which B-Rep face
    /// each mesh face came from — the seam <see cref="TessellateForTetMesh"/> uses to populate
    /// a tet mesher's boundary-condition tags automatically instead of the caller matching
    /// triangles to faces by hand.
    /// <para>The mesh is bit-for-bit <see cref="Tessellate"/>'s output; provenance is a
    /// by-product carried through welding, which drops no non-degenerate polygon and
    /// reorders no face, so face <c>f</c> of the mesh came from the B-Rep face whose index
    /// is <c>result.FaceProvenance[f]</c> (in <c>solid.Faces</c> order — materialise that
    /// same enumeration to turn a <see cref="BrepFace"/> from a query into its tag).</para>
    /// </summary>
    public static TessellationProvenance TessellateWithProvenance(
        BrepSolid solid, int segmentsPerCircle = 32, int curveSamples = 24,
        ProgressCancel? progress = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var faceProvenance = new List<int>();
        var mesh = TessellateCore(solid, segmentsPerCircle, curveSamples, progress, logger,
            faceProvenance, out var perFaceTags);
        return new TessellationProvenance(mesh, perFaceTags!);
    }

    /// <summary>
    /// Lowers <paramref name="solid"/> to a <b>triangulated</b> surface mesh plus the
    /// per-triangle B-Rep-face tags a tet mesher wants — the whole bridge from a B-Rep to a
    /// <c>TetMesher.Mesh(mesh, new TetMeshOptions { FacetTags = tags })</c> call, so a
    /// boundary condition can be named with the <c>BrepQueries</c>/selection vocabulary
    /// instead of by matching triangles to faces by hand.
    /// <para><paramref name="Mesh"/> is all triangles, so a tet mesher's own
    /// <c>Triangulated()</c> is a no-op that preserves order, and <paramref name="FacetTags"/>
    /// is indexed by the mesh's own triangle order — i.e. it lines up with
    /// <c>surface.Triangulated().ToIndexed()</c>, which is exactly what <c>TetFacet.SourceTriangle</c>
    /// indexes into. Each tag is the index, in <c>solid.Faces</c> enumeration order, of the
    /// face the triangle lies on; materialise that same enumeration
    /// (<c>solid.Faces.ToList()</c>) to turn a <see cref="BrepFace"/> from a query into the
    /// tag that selects its facets.</para>
    /// </summary>
    /// <returns>A triangulated surface mesh and one tag per triangle, in the mesh's face order.</returns>
    public static (HalfEdgeMesh Mesh, int[] FacetTags) TessellateForTetMesh(
        BrepSolid solid, int segmentsPerCircle = 32, int curveSamples = 24,
        ProgressCancel? progress = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var (welded, faceProvenance) =
            TessellateWithProvenance(solid, segmentsPerCircle, curveSamples, progress, logger);

        // Triangulating fans each welded face f into (degree(f) − 2) triangles, in face order
        // (HalfEdgeMesh.Triangulated walks ToIndexed()'s faces and Build preserves order), so
        // the per-triangle tags are the per-face tag repeated (degree − 2) times — computed
        // from the face DEGREES alone, without reproducing PolygonFan's diagonal choice, which
        // does not affect the tag (both triangles of a quad share their face).
        var (_, faces) = welded.ToIndexed();
        var triangulated = welded.Triangulated();
        var tags = new int[triangulated.FaceCount];
        int t = 0;
        for (int f = 0; f < faces.Count; f++)
        {
            int triangles = faces[f].Length - 2;
            for (int k = 0; k < triangles; k++)
                tags[t++] = faceProvenance[f];
        }
        if (t != tags.Length)
            throw new InvalidOperationException(
                $"Triangulation produced {tags.Length} triangles but tagging expected {t}; " +
                "the face-degree-to-triangle-count identity was violated.");
        return (triangulated, tags);
    }

    /// <summary>
    /// The shared body of <see cref="Tessellate"/> and <see cref="TessellateWithProvenance"/>.
    /// When <paramref name="faceProvenance"/> is non-null it collects, per polygon, the index
    /// of the B-Rep face (in <c>solid.Faces</c> order) that produced it, and the weld carries
    /// those onto the surviving faces (<paramref name="perFaceProvenance"/>). With it null the
    /// arithmetic is identical bar the untagged weld overload, so the incumbent path is
    /// bit-for-bit unchanged.
    /// </summary>
    private static HalfEdgeMesh TessellateCore(
        BrepSolid solid, int segmentsPerCircle, int curveSamples,
        ProgressCancel? progress, Microsoft.Extensions.Logging.ILogger? logger,
        List<int>? faceProvenance, out int[]? perFaceProvenance)
    {
        if (segmentsPerCircle < 3) throw new ArgumentOutOfRangeException(nameof(segmentsPerCircle));
        if (curveSamples < 2) throw new ArgumentOutOfRangeException(nameof(curveSamples));
        var stopwatch = logger is null ? null : System.Diagnostics.Stopwatch.StartNew();

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
        int faceIndex = -1;
        foreach (var face in solid.Faces)
        {
            faceIndex++;
            progress?.ThrowIfCancelled();
            int before = polygons.Count;
            TessellateFace(face, edgePolylines, segmentsPerCircle, curveSamples, polygons);
            // Each face appends a contiguous run of polygons; record which face made each.
            for (int i = before; i < polygons.Count; i++)
                faceProvenance?.Add(faceIndex);
            progress?.Report(EdgePhase + FacePhase * ++facesDone / Math.Max(1, faceCount));
        }

        progress?.ThrowIfCancelled();
        // Zip seams: cap triangulation may merge exactly-collinear boundary runs (earcut
        // filters them), leaving T-junctions against the finer neighboring faces; zipping
        // reinserts the missing vertices so the mesh closes.
        // 1e-9 = Tolerance.Default.Linear: the absolute weld tolerance — geometry that
        // must weld is constructed exactly, so this must NOT be loosened to hide cracks.
        HalfEdgeMesh mesh;
        if (faceProvenance is null)
        {
            perFaceProvenance = null;
            mesh = MeshWelder.WeldPolygons(polygons, tolerance: 1e-9, zipSeams: true);
        }
        else
        {
            mesh = MeshWelder.WeldPolygons(
                polygons, faceProvenance, out perFaceProvenance, tolerance: 1e-9, zipSeams: true);
        }
        progress?.Report(1);
        if (logger is not null)
            KernelLog.TessellationCompleted(logger, solid.Faces.Count(), mesh.FaceCount,
                stopwatch!.Elapsed.TotalMilliseconds);
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
            case CylinderSurface when IsRingPairedBand(face, edgePolylines):
                TessellateCylinderBand(face, edgePolylines, polygons);
                break;
            case CylinderSurface:
                if (!TrimmedFaceTessellator.TryTessellate(
                        face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out string? cylinderFailure))
                    throw new NotSupportedException(
                        "Cylindrical faces must be ring-paired two-ring bands, or trimmed regions the " +
                        "parameter-space path can handle. " +
                        Diagnose(face, edgePolylines, segmentsPerCircle, curveSamples, cylinderFailure));
                break;
            case HelicalSurface helical when IsFullHelicalBand(face):
                TessellateHelicalBand(face, helical, edgePolylines, polygons);
                break;
            case HelicalSurface:
                // A thread band cut by anything other than its own cap planes — a
                // cross-hole, an angled face, an end chamfer. u is NOT periodic here (z
                // advances with every turn), so every loop has winding 0 and the trimmed
                // path takes its non-wrapping tiers. There is nothing to fall back to: a
                // helical surface has no natural grid either, its "grid" being the sheared
                // rail-to-rail one the full-band path builds out of the face's own edges.
                if (!TrimmedFaceTessellator.TryTessellate(
                        face, edgePolylines, segmentsPerCircle, curveSamples, polygons, out string? helicalFailure))
                    throw new NotSupportedException(
                        "Helical faces must be full bands (two helix rails + two cap spiral cuts), or " +
                        "trimmed regions the parameter-space path can handle. " +
                        Diagnose(face, edgePolylines, segmentsPerCircle, curveSamples, helicalFailure));
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
        // evaluation would reject mid-chord samples as off-surface. Routed through
        // FaceGeometry's rule rather than restated here — a CurveSegment WRAPPING a
        // polyline (what the face splitter hands back after a cut) needs the base's
        // vertices mapped through the segment's reparameterization, which the local
        // version silently missed, dropping such edges onto the uniform path.
        if (FaceGeometry.IsPolylineBacked(edge.Curve))
        {
            var parameters = FaceGeometry.ExactSampleParameters(
                edge.Curve, domain.Start, domain.End, curveSamples);
            // A closed polyline carries no duplicate endpoint.
            if (edge.IsClosedEdge)
                parameters.RemoveAt(parameters.Count - 1);
            var points = new List<Vector3d>(parameters.Count);
            foreach (double t in parameters)
                points.Add(edge.Curve.PointAt(t));
            // A tracer polyline's sample count was fixed at boolean time, so at high
            // densities the grid outpaces it and the facets straddling it degrade
            // (measured 0.9988 → 0.3229 worst normal agreement at 32 → 192 on a
            // band-crossing bore). When the curve carries its two exact carriers,
            // refine each chord back onto the exact intersection at this density.
            // `Underlying` is used as the TYPE hint it is — every refined POINT is
            // solved on the exact surfaces, and the baked vertices pass through
            // verbatim, so a coarse density (or a carrier with no implicit form)
            // reproduces today's polyline bit for bit.
            // An OPEN tracer branch refines whatever loop it bounds — outer-loop
            // crossing curves AND hole-loop chains, now that the band-with-holes tier
            // carries per-slab interior rows and the periodic band threads a scalloped
            // chain (the torus-cut-with-a-bore member's worst 192/96 agreement moved
            // 0.0198 → 0.96 the day the gate widened past outer loops). A CLOSED
            // branch — a bore rim wholly interior to one band — deliberately keeps its
            // baked density: refining one was MEASURED to buy nothing (a radially
            // bored torus's interior rim taken 74 → 287 samples still refuses at
            // 192/96, because the hole-adjacent slabs' anchored rows fail their area
            // guard either way — the residual filed in todo.md — and every density
            // below was already clean), so widening there would spend vertices on
            // every interior rim in the repository for no measured gain.
            if (edge.Curve.Underlying is PolylineCurve3d { IsClosed: false, Carriers: { } carriers })
                return RefineTracerChords(points, carriers, segmentsPerCircle);
            return points;
        }

        // Helix rails and their cap-plane spiral cuts sample proportionally to their
        // turning angle (the parameter IS the angle for both types): a rail spanning N
        // turns gets N·segmentsPerCircle segments, a cut spanning a fraction of a turn
        // the matching fraction. Helical band grids derive their column/row counts from
        // these same polylines, so the sampling agrees by construction.
        // <para>A <see cref="HelicalArcCut3d"/> joins them with ONE difference that is the
        // whole point of it: the cut of an ARC generator crosses the generator's own
        // angular sweep as well as its span in u, and the arc is where the curvature is —
        // a clearance root fillet turns 60 degrees across a u span of a couple of degrees.
        // <c>TurningAngle</c> therefore reports the LARGER of the two for that type, which
        // is also what sets the band grid's v rows, since those are read off this very
        // polyline.</para>
        if (edge.Curve.Underlying is Helix3d or SpiralArc3d or HelicalArcCut3d)
        {
            int n = AngularSegments(TurningAngle(edge.Curve, domain), segmentsPerCircle);
            var points = new List<Vector3d>(n + 1);
            // An arc-generator cut is sampled at uniform GENERATOR ANGLE rather than at
            // uniform u, because v is linear in the angle and NOT in u: the band grid pairs
            // these samples with interior rows at uniform v, and sampling the other way
            // shears every quad against the cap it neighbours (measured: 308 folded facets
            // on a 0.05 clearance rod at 16 segments, and a residual that grew with
            // density). The two ends are taken from the domain verbatim, so the shared
            // rail vertices stay bit-exact.
            var arcCut = edge.Curve as HelicalArcCut3d;
            double startAngle = arcCut?.AngleAt(domain.Start) ?? 0;
            double endAngle = arcCut?.AngleAt(domain.End) ?? 0;
            for (int i = 0; i <= n; i++)
            {
                double fraction = (double)i / n;
                double t = arcCut is null || i == 0 || i == n
                    ? domain.ParameterAt(fraction)
                    : arcCut.ParameterAtAngle(startAngle + (endAngle - startAngle) * fraction);
                points.Add(edge.Curve.PointAt(t));
            }
            return points;
        }

        if (edge.IsClosedEdge)
        {
            int n = IsAngularlyParameterized(edge.Curve) ? segmentsPerCircle : curveSamples;
            var points = new List<Vector3d>(n);
            for (int i = 0; i < n; i++)
                points.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / n)));
            return points;
        }

        // A straight edge carries no curvature, so two samples describe it exactly — unless
        // it bounds a face whose parameter is an ANGLE and it is not iso-parameter on it,
        // where the two samples describe the CURVE exactly and the FACE not at all. See
        // StraightEdgeSegments. Both ends stay the incumbent expressions, so a one-segment
        // answer (every straight edge in the repo before this rule) is bit-identical.
        if (edge.Curve.Underlying is Line3d)
        {
            int n = StraightEdgeSegments(edge, segmentsPerCircle, curveSamples);
            if (n <= 1)
                return [edge.Curve.PointAt(domain.Start), edge.Curve.PointAt(domain.End)];
            var points = new List<Vector3d>(n + 1) { edge.Curve.PointAt(domain.Start) };
            for (int i = 1; i < n; i++)
                points.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / n)));
            points.Add(edge.Curve.PointAt(domain.End));
            return points;
        }

        // An OPEN angular edge — a circle or an ellipse cut into arcs by a boolean, which
        // is what every split rim is — must resolve the natural grid COLUMNS its span
        // crosses as well as its own curvature, so it takes the larger of the two counts.
        // <para>Without the angular half it took `curveSamples` and nothing else: a fixed
        // count at every density, which is a FLOOR (the same shape as the recorded
        // baked-tracer-polyline and CurveSegment-turning-angle findings, and the fourth
        // occurrence of `Underlying` being a TYPE hint that says nothing about the
        // parameter mapping — the closed case asks `IsAngularlyParameterized` and the open
        // one did not). Measured on a threaded rod's 5%-depth chamfer cone, whose rim is
        // three spiral arcs and one arc of the new cap circle: the spirals scaled 5/9/17/33
        // with the density while the circle piece sat at 25 at 32, 64, 128 AND 256, and the
        // strip's worst facet-vs-surface agreement was 0.1301 against a floor of
        // 0.8315.</para>
        // <para><b>The MAXIMUM of the two counts, never a replacement — which was MEASURED
        // rather than preferred.</b> Replacing `curveSamples` is the tidier rule and it
        // makes the default density measurably WORSE: at the default 32/24 a sub-half-turn
        // arc is finer under `curveSamples` than under the angular count, so replacing it
        // COARSENS every split rim in the repository — a partial revolve's tessellated
        // volume stopped matching its exact closed form (2.35451265 against 2.35146969, a
        // discrete identity turned into an approximation), a slot pocket left its stated
        // chordal-error band, and 19 of the Interop suite's tests moved. The maximum is
        // monotone: no edge anywhere gets coarser, so a change here can only add fidelity,
        // which is the whole safety argument for touching a shared sampling rule.</para>
        if (IsAngularlyParameterized(edge.Curve))
        {
            int angular = AngularSegments(TurningAngle(edge.Curve, domain), segmentsPerCircle);
            int n = Math.Max(curveSamples, angular);
            var points = new List<Vector3d>(n + 1);
            for (int i = 0; i <= n; i++)
                points.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / n)));
            return points;
        }

        // A rail of a loft that is AFFINE in v is an exact straight segment (the lerp of
        // its two sections' endpoints), and the band's own grid collapses its v columns to
        // the two section rows (GridParams asks the same IsAffineInV) — so the pair agrees
        // by one rule. Sampling it densely instead hands every planar neighbour a
        // near-collinear run its ear clipping degenerates on (measured: 18 of 23 facets
        // on a variable run's front face were forced sliver ears).
        if (edge.Curve.Underlying is LoftRailCurve rail && rail.Surface.IsAffineInV)
            return [edge.Curve.PointAt(domain.Start), edge.Curve.PointAt(domain.End)];

        var samples = new List<Vector3d>(curveSamples + 1);
        for (int i = 0; i <= curveSamples; i++)
            samples.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / curveSamples)));
        return samples;
    }

    /// <summary>
    /// Inserts refined samples between a traced polyline's baked vertices wherever a
    /// chord subtends more than one natural angular step (<c>2π/segmentsPerCircle</c>) of
    /// the exact intersection of the two carriers. Every inserted point is solved onto
    /// BOTH exact surfaces by <see cref="SurfaceCorner.TrySolvePoint"/>'s minimum-norm
    /// Newton and accepted only at the weld tier, so refined rims weld exactly like baked
    /// vertices do; both adjacent faces share the one refined list because
    /// <see cref="SampleEdge"/> fills the edge-polyline table once per tessellation.
    /// <para>Every guard errs toward KEEPING the chord — a chord kept reproduces the
    /// pre-refinement tessellation, while a bad midpoint accepted would be invented
    /// geometry: the solve must converge to 1e-9 (weld tier), the midpoint must project
    /// strictly between the chord's ends, and its sagitta may not exceed the chord (a
    /// solve that jumped to another intersection branch). The split criterion is the
    /// osculating-circle identity θ ≈ 8·sagitta/length, so "sagitta above
    /// length·π/(4n)" IS "the chord subtends more than 2π/n"; a straight stretch
    /// measures a weld-tier sagitta and never splits.</para>
    /// </summary>
    private static List<Vector3d> RefineTracerChords(
        List<Vector3d> points, (Surface A, Surface B) carriers, int segmentsPerCircle)
    {
        Surface[] pair = [carriers.A, carriers.B];
        var refined = new List<Vector3d>(points.Count);
        for (int i = 0; i + 1 < points.Count; i++)
        {
            refined.Add(points[i]);
            AppendRefined(points[i], points[i + 1], pair, segmentsPerCircle, 6, refined);
        }
        refined.Add(points[^1]);
        return refined;
    }

    private static void AppendRefined(
        in Vector3d from, in Vector3d to, Surface[] carriers,
        int segmentsPerCircle, int depth, List<Vector3d> refined)
    {
        if (depth == 0)
            return;
        var chord = to - from;
        double length = chord.Length;
        // Weld tier: below it there is nothing a finer sample could express.
        if (length <= Tolerance.Default.Linear)
            return;
        if (!SurfaceCorner.TrySolvePoint(carriers, (from + to) * 0.5, out var corner, out _)
            || corner.Residual > Tolerance.Default.Linear)
            return; // no implicit form, or not a weld-grade point: keep the chord
        var offset = corner.Point - from;
        double along = offset.Dot(chord) / chord.LengthSquared;
        var deviation = offset - chord * along;
        double sagitta = deviation.Length;
        if (sagitta <= Math.Max(Tolerance.Default.Linear, length * (Math.PI / (4 * segmentsPerCircle))))
            return; // flat enough for this density
        if (along <= 0 || along >= 1 || sagitta > length)
            return; // the solve left the chord's own span: another branch, keep the chord
        AppendRefined(from, corner.Point, carriers, segmentsPerCircle, depth - 1, refined);
        refined.Add(corner.Point);
        AppendRefined(corner.Point, to, carriers, segmentsPerCircle, depth - 1, refined);
    }

    /// <summary>
    /// Whether a closed curve's parameter IS an angle over one turn, which is what earns
    /// the <c>segmentsPerCircle</c> density rather than the generic <c>curveSamples</c>.
    /// <para>Circles and ELLIPSES both qualify — an ellipse is <c>C + A·cos θ + B·sin θ</c>,
    /// so a caller asking for 256 segments per circle gets 256 around an ellipse too. It
    /// used to get 32, which is not a tolerance question but a wrong ANSWER to the density
    /// the caller stated: an elliptical prism measured 0.64% under its analytic πabh at
    /// "256 segments", the deficit of a 23-gon. The same rule reaches the elliptical edges
    /// <c>SurfaceIntersection</c> already produces for an oblique plane through a cylinder,
    /// which were under-sampled the same way.</para>
    /// </summary>
    private static bool IsAngularlyParameterized(Curve3d curve) =>
        curve.Underlying is Circle3d or Ellipse3d;

    /// <summary>
    /// Parameter samples over a curve's full domain, matching <see cref="SampleEdge"/>'s
    /// rules exactly so face grids and shared boundary edges weld without cracks.
    /// </summary>
    private static double[] CurveParams(Curve3d curve, int segmentsPerCircle, int curveSamples)
    {
        var domain = curve.Domain;
        if (curve.IsClosed)
        {
            int n = IsAngularlyParameterized(curve) ? segmentsPerCircle : curveSamples;
            return EvenParams(domain, n, includeEnd: false);
        }
        if (curve.Underlying is Line3d)
            return [domain.Start, domain.End];
        // The open angular rule SampleEdge applies, restated nowhere: face grids and the
        // boundary edges they weld to must round to the same count.
        if (IsAngularlyParameterized(curve))
        {
            return EvenParams(
                domain,
                Math.Max(curveSamples, AngularSegments(TurningAngle(curve, domain), segmentsPerCircle)),
                includeEnd: true);
        }
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
        // SampleEdge) and v takes the generic curve density the rails are sampled at —
        // EXCEPT where the blend is affine in v (a ruled two-section loft), where a
        // v-chord lies exactly on the surface and the grid collapses to the two section
        // rows, matching SampleEdge's straight-segment rail sampling (one rule, both
        // sides — the helical band's infinite-v-step precedent).
        LoftedSurface lofted => (
            EvenParams(lofted.DomainU, lofted.NaturalUSegments(segmentsPerCircle, curveSamples),
                includeEnd: !lofted.IsClosedU),
            lofted.IsAffineInV
                ? [lofted.DomainV.Start, lofted.DomainV.End]
                : EvenParams(lofted.DomainV, curveSamples, includeEnd: true),
            lofted.IsClosedU, false),
        _ => throw new NotSupportedException($"No grid sampling for {surface.GetType().Name}."),
    };

    /// <summary>
    /// Whether <see cref="TessellateCylinderBand"/>'s index pairing is VALID for this face —
    /// a statement about what the loops are, not merely about how many there are.
    /// <para>That path is the ring-driven one: it emits one quad per sample index j joining
    /// <c>bottom[j]</c> to <c>top[j]</c>, which is the correct band exactly when the two
    /// polylines sample the SAME azimuths in the same order. Two natural rings do — both are
    /// circles on the cylinder's own frame sampled at identical parameters, so their radial
    /// parts agree to a few ulps — and that is why a plain cylinder needs no parameter grid at
    /// all. Two INDEPENDENTLY traced wrapping cuts do not: a cross-drill piercing the wall
    /// leaves the band bounded by two marching-tracer polylines with unrelated phases, and
    /// pairing those by index folds the band (measured on a Ø3 cross-drill through a Ø10
    /// cylinder: 18 of 40 quads faced inward, worst facet-vs-surface normal agreement −0.0000,
    /// and the weld then reported a duplicated directed edge). Such faces go to
    /// <see cref="TrimmedFaceTessellator"/>, which pairs by pulled-back u instead.</para>
    /// <para>So this is not a filter in front of a working path — it is that path's own
    /// correctness condition, checked rather than assumed. The old test (two loops, one closed
    /// coedge each) admitted exactly the case it could not triangulate.</para>
    /// </summary>
    /// <summary>
    /// Whether this is the FULL helical band <see cref="TessellateHelicalBand"/>'s sheared
    /// grid describes: one loop of four coedges — two <see cref="Helix3d"/> rails at
    /// v = 0 and v = 1, and two PLANAR <see cref="SpiralArc3d"/> cap cuts at the ends of
    /// u. That is every band <c>SolidFactory.MakeThreadedRod</c> builds, and the shape
    /// whose interior columns can be interpolated linearly between exactly projected rail
    /// corners. Anything else — a band a cross-hole, an angled face or an end chamfer has
    /// trimmed — goes to <see cref="TrimmedFaceTessellator"/>.
    /// <para><b>Planar is the load-bearing word</b>, and it is the same lesson
    /// <see cref="IsRingPairedBand"/> records: a gate should BE the path's correctness
    /// condition rather than a proxy for it. A coaxial CONE cuts a helical band in a
    /// <see cref="SpiralArc3d"/> too — the conical spiral of a 45-degree end chamfer — so
    /// counting spiral edges alone would send a chamfered band down a grid that assumes
    /// its two cuts are the ends of u, and interpolate columns across a boundary that
    /// runs diagonally instead.</para>
    /// </summary>
    private static bool IsFullHelicalBand(BrepFace face) =>
        face.Loops.Count == 1 &&
        face.OuterLoop.Coedges.Count == 4 &&
        face.OuterLoop.Coedges.Where(c => c.Edge.Curve.Underlying is Helix3d)
            .Select(c => c.Edge).Distinct().Count() == 2 &&
        face.OuterLoop.Coedges.Where(c => IsPlanarHelicalCut(c.Edge.Curve.Underlying))
            .Select(c => c.Edge).Distinct().Count() == 2;

    /// <summary>A cap cut of either generator family: axis-perpendicular by the same
    /// exact-zero test on both types.</summary>
    private static bool IsPlanarHelicalCut(Curve3d curve) => curve switch
    {
        SpiralArc3d spiral => spiral.IsPlanar,
        HelicalArcCut3d arcCut => arcCut.IsPlanar,
        _ => false,
    };

    private static bool IsRingPairedBand(BrepFace face, Dictionary<BrepEdge, List<Vector3d>> edgePolylines)
    {
        if (face.Loops.Count != 2 ||
            !face.Loops.All(l => l.Coedges.Count == 1 && l.Coedges[0].Edge.IsClosedEdge))
            return false;

        var cylinder = (CylinderSurface)face.Surface;
        var axis = cylinder.Axis.Normalized();
        var first = edgePolylines[face.Loops[0].Coedges[0].Edge];
        var second = edgePolylines[face.Loops[1].Coedges[0].Edge];
        if (first.Count != second.Count)
            return false;

        // Both samples lie on the same cylinder, so their radial vectors share a magnitude
        // (the radius) and comparing them directly compares the AZIMUTH. 1e-9 is the absolute
        // weld tier, which is the right one: rings that pair are exactly constructed on one
        // frame, so anything that fails here was never index-pairable.
        Vector3d Radial(in Vector3d p)
        {
            var d = p - cylinder.Origin;
            return d - axis * d.Dot(axis);
        }
        for (int j = 0; j < first.Count; j++)
        {
            if ((Radial(first[j]) - Radial(second[j])).Length > Tolerance.Default.Linear)
                return false;
        }
        return true;
    }

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
        SegmentsForSteps(Math.Abs(span) * segmentsPerCircle / (2 * Math.PI));

    /// <summary>The Ceiling rule above, over a count of natural grid steps already
    /// measured. One rule, so an edge and the grid it must weld to cannot round apart.</summary>
    private static int SegmentsForSteps(double steps) =>
        Math.Max(1, (int)Math.Ceiling(steps - 1e-9));

    /// <summary>
    /// How many segments a STRAIGHT edge is sampled at. Two, everywhere except where the
    /// edge crosses the natural grid COLUMNS of a face whose parameter is an angle.
    ///
    /// <para>The trap is that a straight curve is described exactly by its endpoints while
    /// the FACE it bounds may not be: a <c>Drill</c> tool's flat bottom is a full-turn
    /// <see cref="RevolvedSurface"/> whose u is an azimuth about the pole, so a face
    /// crossing it obliquely cuts it along a CHORD — and a chord's two endpoints both sit on
    /// the rim, at v = 1, exactly where the arc completing the loop already is. The pulled
    /// back loop is then a zero-area sliver running out along v = 1 and back, and the
    /// trimmed tessellator refuses it as a winding structure it cannot read, however dense
    /// the grid around it becomes. Measured: a Ø6 blind drill breaking out of a plate's top
    /// face refused at every density with a 2-sample chord, and the SAME chord resampled
    /// (identical geometry, identical endpoints) tessellates.</para>
    ///
    /// <para><b>The gate IS the correctness condition rather than a proxy for it</b>: the
    /// count comes from the AZIMUTH the edge sweeps about the face's own axis, so an
    /// iso-parameter straight edge — a cylinder's or a cone's ruling, a revolve's seam,
    /// a helical band's generator, which is every straight edge on an angular face that
    /// existed before this rule — sweeps nothing and stays at two samples with no separate
    /// test to keep in step. Extra samples on a straight curve carry no fidelity cost
    /// either: every one of them is exactly on the curve, the same argument the
    /// baked-carrier refinement in <see cref="RefineTracerChords"/> makes.</para>
    ///
    /// <para>The count is the MAX over every using face, because <see cref="SampleEdge"/>
    /// fills one polyline per edge and both sides must read it.</para>
    /// </summary>
    private static int StraightEdgeSegments(BrepEdge edge, int segmentsPerCircle, int curveSamples)
    {
        int segments = 1;
        foreach (var use in edge.Uses)
        {
            if (!TryAngularDensity(use.Loop.Face.Surface, segmentsPerCircle, curveSamples,
                    out var origin, out var axis, out double stepsPerRadian))
                continue;
            segments = Math.Max(
                segments, SegmentsForSteps(SweptAzimuth(edge, origin, axis) * stepsPerRadian));
        }
        return segments;
    }

    /// <summary>
    /// Whether a surface's u parameter is an ANGLE about an axis, and at what density the
    /// natural grid samples it. The density is the surface's OWN — a partial revolve spends
    /// <c>curveSamples</c> over its sweep rather than <c>segmentsPerCircle</c> over a full
    /// turn — read from the same rules <see cref="GridParams"/> applies.
    /// </summary>
    private static bool TryAngularDensity(
        Surface surface, int segmentsPerCircle, int curveSamples,
        out Vector3d origin, out Vector3d axis, out double stepsPerRadian)
    {
        switch (surface)
        {
            case CylinderSurface cylinder:
                (origin, axis, stepsPerRadian) =
                    (cylinder.Origin, cylinder.Axis, segmentsPerCircle / (2 * Math.PI));
                return true;
            case RevolvedSurface revolved:
                (origin, axis) = (revolved.AxisOrigin, revolved.AxisDirection);
                stepsPerRadian = revolved.IsFullTurn
                    ? segmentsPerCircle / (2 * Math.PI)
                    : curveSamples / Math.Abs(revolved.DomainU.Length);
                return true;
            case HelicalSurface helical:
                (origin, axis, stepsPerRadian) =
                    (helical.Frame.Origin, helical.Frame.Z, segmentsPerCircle / (2 * Math.PI));
                return true;
        }
        (origin, axis, stepsPerRadian) = (default, default, 0);
        return false;
    }

    /// <summary>
    /// The total azimuth an edge's radial sweeps about an axis — the number of natural u
    /// columns it crosses, once multiplied by the density. Accumulated as SHORTEST steps
    /// between samples (<c>WrapsWholeCylinder</c>'s rule) so a run straddling the seam
    /// measures its true span, and samples whose radial vanishes are skipped: a chord
    /// through a disk's pole has no azimuth exactly there, and the half turn across it is
    /// carried by the samples either side.
    /// </summary>
    private static double SweptAzimuth(BrepEdge edge, in Vector3d origin, in Vector3d axisDirection)
    {
        if (!axisDirection.TryNormalize(Tolerance.Default, out var axis))
            return 0;
        var x = axis.ArbitraryPerpendicular(Tolerance.Default);
        var y = axis.Cross(x);
        var domain = edge.Domain;
        const int samples = 32;
        double swept = 0, previous = 0;
        bool have = false;
        // Scale-free: a radial is degenerate relative to the edge's own reach from the axis.
        double extent = 0;
        Span<Vector3d> radials = stackalloc Vector3d[samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            var offset = edge.Curve.PointAt(domain.ParameterAt((double)i / samples)) - origin;
            radials[i] = offset - axis * offset.Dot(axis);
            extent = Math.Max(extent, radials[i].Length);
        }
        if (extent <= 0)
            return 0;
        for (int i = 0; i <= samples; i++)
        {
            if (radials[i].Length <= extent * 1e-9)
                continue;
            double angle = Math.Atan2(radials[i].Dot(y), radials[i].Dot(x));
            if (have)
            {
                double delta = angle - previous;
                if (delta > Math.PI)
                    delta -= 2 * Math.PI;
                else if (delta < -Math.PI)
                    delta += 2 * Math.PI;
                swept += Math.Abs(delta);
            }
            previous = angle;
            have = true;
        }
        return swept;
    }

    /// <summary>
    /// The angle an edge riding a <see cref="Helix3d"/> or <see cref="SpiralArc3d"/> turns
    /// through, measured in the carrier's OWN parameter — which is the angle.
    /// <para>For a raw carrier that is just the edge's domain length, but a
    /// <see cref="CurveSegment"/> — what the face splitter hands back after every cut —
    /// reparameterizes to [0, 1] while <c>Underlying</c> still points at the spiral, so
    /// reading the domain there measures a segment FRACTION as if it were radians. Every
    /// such edge got the same count whatever it spanned (11 at segmentsPerCircle = 64,
    /// and 11 at 256 as well: a density FLOOR, exactly the shape of the baked-tracer-polyline
    /// finding). On a chamfered thread it put two cuts of the same 0.785 rad span at 8 and
    /// 11 samples, which the sheared helical grid reports as "boundary polylines disagree
    /// in sample count".</para>
    /// <para>Third occurrence of one rule: <b><c>Underlying</c> is a TYPE hint and says
    /// nothing about the parameter mapping</b> (see <c>FaceGeometry.ExactSampleParameters</c>,
    /// which exists for the same reason on the polyline side).</para>
    /// </summary>
    private static double TurningAngle(Curve3d curve, in Interval domain)
    {
        // An arc-generator cut turns in TWO angles at once — the band's phase u (the
        // parameter) and the generator's own polar angle — and it is the second that
        // carries the curvature, so the count must resolve whichever is larger.
        if (curve is HelicalArcCut3d arcCut)
            return Math.Max(
                Math.Abs(domain.Length),
                Math.Abs(arcCut.AngleAt(domain.End) - arcCut.AngleAt(domain.Start)));
        if (curve is not CurveSegment segment)
            return Math.Abs(domain.Length);
        double s0 = segment.BaseStart + (segment.BaseEnd - segment.BaseStart) * domain.Start;
        double s1 = segment.BaseStart + (segment.BaseEnd - segment.BaseStart) * domain.End;
        return TurningAngle(segment.Base, new Interval(Math.Min(s0, s1), Math.Max(s0, s1)));
    }

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
        var coedges = face.OuterLoop.Coedges;
        var railEdges = coedges.Where(c => c.Edge.Curve.Underlying is Helix3d).Select(c => c.Edge).Distinct().ToList();
        var cutEdges = coedges.Where(c => c.Edge.Curve.Underlying is SpiralArc3d or HelicalArcCut3d)
            .Select(c => c.Edge).Distinct().ToList();

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
        // Every column of the band is the SAME curve in (u, v) translated along u — the
        // cut at height z sits at u(v) = (z − z_generator(v))/rate — so the interior
        // column at v is its own u at v = 0 plus that shear. For a STRAIGHT generator the
        // shear is affine, which is exactly what the incumbent lerp between the two rails
        // computes, so it is kept verbatim there. For an ARC generator it is not: on a
        // 0.2 mm clearance fillet the chord's sagitta is ~0.17 rad of phase against a
        // column spacing of ~0.10, so the first interior column would land OUTSIDE the cap
        // it neighbours and the mesh would poke past the end face.
        bool straight = surface.IsStraightGenerator;
        double axialAtZero = surface.AxialAt(0);
        double rate = surface.AxialRate;
        for (int j = 1; j < n; j++)
        {
            double f = (double)j / n;
            double uBottom = uBottomStart + (uBottomEnd - uBottomStart) * f;
            double uTop = uTopStart + (uTopEnd - uTopStart) * f;
            for (int k = 1; k < m; k++)
            {
                double v = (double)k / m;
                double u = straight
                    ? uBottom + (uTop - uBottom) * v
                    : uBottom + (axialAtZero - surface.AxialAt(v)) / rate;
                grid[j, k] = surface.PointAt(u, v);
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
