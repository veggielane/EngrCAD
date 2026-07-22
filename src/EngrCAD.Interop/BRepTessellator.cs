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
                default:
                    throw new NotSupportedException(
                        $"Tessellation of {face.Surface.GetType().Name} faces is not implemented yet.");
            }
        }

        return MeshWelder.WeldPolygons(polygons, tolerance: 1e-9);
    }

    private static List<Vector3d> SampleEdge(BrepEdge edge, int segmentsPerCircle, int curveSamples)
    {
        var domain = edge.Domain;
        if (edge.IsClosedEdge)
        {
            int n = edge.Curve is Circle3d ? segmentsPerCircle : curveSamples;
            var points = new List<Vector3d>(n);
            for (int i = 0; i < n; i++)
                points.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / n)));
            return points;
        }

        if (edge.Curve is Line3d)
            return [edge.Curve.PointAt(domain.Start), edge.Curve.PointAt(domain.End)];

        var samples = new List<Vector3d>(curveSamples + 1);
        for (int i = 0; i <= curveSamples; i++)
            samples.Add(edge.Curve.PointAt(domain.ParameterAt((double)i / curveSamples)));
        return samples;
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
        if (face.Loops.Count != 1)
            throw new NotSupportedException("Planar faces with holes are not supported yet.");

        var boundary = LoopPolyline(face.OuterLoop, edgePolylines);
        var boundary2d = boundary.Select(p => plane.Project(p)).ToList();

        // Triangulator emits CCW triangles in plane coordinates, whose 3D normal is
        // x × y = the plane normal = the outward face normal by construction.
        foreach (var (a, b, c) in PolygonTriangulator.Triangulate(boundary2d))
            polygons.Add([boundary[a], boundary[b], boundary[c]]);
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
