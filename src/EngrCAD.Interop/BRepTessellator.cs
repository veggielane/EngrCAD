using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Interop;

/// <summary>
/// B-Rep → mesh conversion: each edge is sampled once into a shared polyline, faces are
/// triangulated against those polylines (so neighboring faces meet exactly), and the
/// resulting soup is welded into a half-edge mesh. Supported faces so far: planar faces
/// with a single boundary loop (ear clipping in plane coordinates) and full-revolution
/// cylindrical bands. Trimmed NURBS faces are future work.
/// </summary>
public static class BRepTessellator
{
    public static HalfEdgeMesh Tessellate(BrepSolid solid, int segmentsPerCircle = 32, int curveSamples = 24)
    {
        if (segmentsPerCircle < 3) throw new ArgumentOutOfRangeException(nameof(segmentsPerCircle));
        if (curveSamples < 2) throw new ArgumentOutOfRangeException(nameof(curveSamples));

        var edgePolylines = new Dictionary<BrepEdge, List<Vector3d>>();
        foreach (var edge in solid.Edges)
            edgePolylines[edge] = SampleEdge(edge, segmentsPerCircle, curveSamples);

        var polygons = new List<IReadOnlyList<Vector3d>>();
        foreach (var face in solid.Faces)
        {
            switch (face.Surface)
            {
                case PlaneSurface plane:
                    TessellatePlanarFace(face, plane, edgePolylines, polygons);
                    break;
                case CylinderSurface:
                    TessellateCylinderBand(face, edgePolylines, polygons);
                    break;
                case ExtrudedSurface extruded:
                    TessellateGrid(extruded,
                        CurveParams(extruded.Generator, segmentsPerCircle, curveSamples),
                        [0.0, 1.0],
                        closedU: extruded.Generator.IsClosed, closedV: false, polygons);
                    break;
                case RevolvedSurface revolved:
                    // Full turns are periodic in u; partial turns must sample u the same
                    // way their arc rail edges do (curveSamples), so the boundaries weld.
                    // A closed generator (e.g. a revolved circle = pipe elbow) is periodic
                    // in v.
                    TessellateGrid(revolved,
                        revolved.IsFullTurn
                            ? EvenParams(revolved.DomainU, segmentsPerCircle, includeEnd: false)
                            : EvenParams(revolved.DomainU, curveSamples, includeEnd: true),
                        CurveParams(revolved.Generator, segmentsPerCircle, curveSamples),
                        closedU: revolved.IsFullTurn,
                        closedV: revolved.Generator.IsClosed, polygons);
                    break;
                case SweptSurface swept:
                    TessellateGrid(swept,
                        CurveParams(swept.Generator, segmentsPerCircle, curveSamples),
                        EvenParams(swept.Path.Domain, curveSamples, includeEnd: true),
                        closedU: swept.Generator.IsClosed, closedV: false, polygons);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Tessellation of {face.Surface.GetType().Name} faces is not implemented yet.");
            }
        }

        // Zip seams: cap triangulation may merge exactly-collinear boundary runs (earcut
        // filters them), leaving T-junctions against the finer neighboring faces; zipping
        // reinserts the missing vertices so the mesh closes.
        return MeshWelder.WeldPolygons(polygons, tolerance: 1e-9, zipSeams: true);
    }

    private static List<Vector3d> SampleEdge(BrepEdge edge, int segmentsPerCircle, int curveSamples)
    {
        var domain = edge.Domain;
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
                polygons.Add([grid[j, k], grid[j1, k], grid[j1, k1], grid[j, k1]]);
            }
        }
    }

    private static List<Vector3d> LoopPolyline(BrepLoop loop, Dictionary<BrepEdge, List<Vector3d>> edgePolylines)
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

    private static void TessellateCylinderBand(
        BrepFace face,
        Dictionary<BrepEdge, List<Vector3d>> edgePolylines,
        List<IReadOnlyList<Vector3d>> polygons)
    {
        var cylinder = (CylinderSurface)face.Surface;
        if (face.Loops.Count != 2 || face.Loops.Any(l => l.Coedges.Count != 1 || !l.Coedges[0].Edge.IsClosedEdge))
            throw new NotSupportedException(
                "Cylindrical faces are currently limited to full bands bounded by two closed edges.");

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
